using System.Collections.Concurrent;
using dotnet.challenge.data.Cache;
using dotnet.challenge.data.Entities;

namespace dotnet.challenge.api.Services;

/// <summary>
/// Inventory reservation logic.
/// <para>
/// Concurrency model: every operation that reads and then writes a product's stock, or the state of
/// one of its reservations, runs while holding that product's async lock. This makes the
/// check-then-act sequences (stock check + decrement, status check + transition + stock restore)
/// atomic per product, while operations on different products still run in parallel.
/// Entities are immutable records, so lock-free readers always see a consistent snapshot.
/// </para>
/// </summary>
public class InventoryService : IInventoryService
{
    private readonly ISimpleObjectCache<Guid, Product> _products;
    private readonly ISimpleObjectCache<Guid, Reservation> _reservations;
    // Defect #4: the clock is injected and always read as UTC (ExpiresAt is UTC), instead of
    // comparing against local DateTime.Now. It also lets tests drive expiry with a fake clock.
    private readonly TimeProvider _time;

    // Defect #2: one async lock per product. A thread-safe cache isn't enough on its own, because the
    // atomic unit (read stock -> check -> write) spans several cache calls. SemaphoreSlim is used
    // instead of `lock` because the critical section awaits, and per-product keeps products independent.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _productLocks = new();

    // TimeProvider is optional so the provided tests can still use the two-argument constructor.
    public InventoryService(
        ISimpleObjectCache<Guid, Product> products,
        ISimpleObjectCache<Guid, Reservation> reservations,
        TimeProvider? timeProvider = null)
    {
        _products = products;
        _reservations = reservations;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public async Task<Product> CreateProductAsync(string name, int stock, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(stock);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Stock = stock
        };
        await _products.AddAsync(product.Id, product);
        return product;
    }

    public async Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _products.GetAsync(id);
        if (product is null)
        {
            return null;
        }

        // Rule 2: a GET issued after expiry must already see the stock returned. The sweeper runs only
        // once per second, so overdue reservations are expired here too. The fast path takes no lock.
        var overdue = await FindOverdueReservationIdsAsync(id, UtcNow);
        if (overdue.Count == 0)
        {
            return product;
        }

        using (await AcquireProductLockAsync(id, cancellationToken))
        {
            await ExpireOverdueLockedAsync(overdue, UtcNow);
            return await _products.GetAsync(id);
        }
    }

    public async Task<(ReserveResult Result, Reservation? Reservation)> TryReserveAsync(
        Guid productId, int quantity, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttlSeconds);

        if (await _products.GetAsync(productId) is null)
        {
            return (ReserveResult.ProductNotFound, null);
        }

        // Defect #2: the stock check and the decrement now happen under the same lock. Before, two
        // callers could both read Stock = 1, both pass the check and both reserve (overselling).
        using (await AcquireProductLockAsync(productId, cancellationToken))
        {
            // Re-read under the lock: the value seen before acquiring it may already be stale.
            var product = (await _products.GetAsync(productId))!;
            var now = UtcNow;

            if (product.Stock < quantity)
            {
                // Stock held by overdue reservations is available again, even if the sweeper has not run yet.
                var overdue = await FindOverdueReservationIdsAsync(productId, now);
                if (overdue.Count > 0)
                {
                    await ExpireOverdueLockedAsync(overdue, now);
                    product = (await _products.GetAsync(productId))!;
                }

                if (product.Stock < quantity)
                {
                    return (ReserveResult.InsufficientStock, null);
                }
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Quantity = quantity,
                CreatedAt = now,
                ExpiresAt = now.AddSeconds(ttlSeconds),
                Status = ReservationStatus.Active
            };

            await _products.UpdateAsync(productId, product with { Stock = product.Stock - quantity });
            await _reservations.AddAsync(reservation.Id, reservation);

            return (ReserveResult.Success, reservation);
        }
    }

    public async Task<Reservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var reservation = await _reservations.GetAsync(id);
        if (reservation is null || !IsOverdue(reservation, UtcNow))
        {
            return reservation;
        }

        using (await AcquireProductLockAsync(reservation.ProductId, cancellationToken))
        {
            return await ExpireIfOverdueLockedAsync(id, UtcNow);
        }
    }

    public async Task<ConfirmResult> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var reservation = await _reservations.GetAsync(reservationId);
        if (reservation is null)
        {
            return ConfirmResult.NotFound;
        }

        using (await AcquireProductLockAsync(reservation.ProductId, cancellationToken))
        {
            // An Active reservation past its expiry is Expired, whether or not the sweeper got to it yet.
            var current = (await ExpireIfOverdueLockedAsync(reservationId, UtcNow))!;

            switch (current.Status)
            {
                case ReservationStatus.Confirmed:
                    return ConfirmResult.AlreadyConfirmed;

                case ReservationStatus.Active:
                    await _reservations.UpdateAsync(reservationId, current with { Status = ReservationStatus.Confirmed });
                    return ConfirmResult.Success;

                default: // Released or Expired
                    return ConfirmResult.IllegalState;
            }
        }
    }

    public async Task<ReleaseResult> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var reservation = await _reservations.GetAsync(reservationId);
        if (reservation is null)
        {
            return ReleaseResult.NotFound;
        }

        using (await AcquireProductLockAsync(reservation.ProductId, cancellationToken))
        {
            var current = (await ExpireIfOverdueLockedAsync(reservationId, UtcNow))!;

            switch (current.Status)
            {
                case ReservationStatus.Released:
                case ReservationStatus.Expired:
                    return ReleaseResult.AlreadyReleased;

                default: // Active or Confirmed
                    await ReturnStockLockedAsync(current.ProductId, current.Quantity);
                    await _reservations.UpdateAsync(reservationId, current with { Status = ReservationStatus.Released });
                    return ReleaseResult.Success;
            }
        }
    }

    public async Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var overdueByProduct = (await _reservations.GetAllAsync())
            .Where(r => IsOverdue(r, now))
            .GroupBy(r => r.ProductId, r => r.Id);

        var expired = 0;
        foreach (var group in overdueByProduct)
        {
            using (await AcquireProductLockAsync(group.Key, cancellationToken))
            {
                expired += await ExpireOverdueLockedAsync(group, now);
            }
        }
        return expired;
    }

    /// <summary>
    /// True if the reservation is still Active but its expiry (UTC) has passed.
    /// </summary>
    public static bool IsOverdue(Reservation reservation, DateTime utcNow) =>
        reservation.Status == ReservationStatus.Active && reservation.ExpiresAt <= utcNow;

    private async Task<List<Guid>> FindOverdueReservationIdsAsync(Guid productId, DateTime utcNow) =>
        (await _reservations.GetAllAsync())
            .Where(r => r.ProductId == productId && IsOverdue(r, utcNow))
            .Select(r => r.Id)
            .ToList();

    // ---- The methods below must only be called while holding the owning product's lock. ----

    private async Task<int> ExpireOverdueLockedAsync(IEnumerable<Guid> reservationIds, DateTime utcNow)
    {
        var expired = 0;
        foreach (var id in reservationIds)
        {
            var (_, changed) = await TryExpireLockedAsync(id, utcNow);
            if (changed)
            {
                expired++;
            }
        }
        return expired;
    }

    private async Task<Reservation?> ExpireIfOverdueLockedAsync(Guid reservationId, DateTime utcNow) =>
        (await TryExpireLockedAsync(reservationId, utcNow)).Current;

    /// <summary>
    /// Re-reads the reservation and, if it is overdue, marks it Expired and returns its quantity to stock.
    /// Returns the current state of the reservation and whether this call expired it.
    /// </summary>
    private async Task<(Reservation? Current, bool Expired)> TryExpireLockedAsync(Guid reservationId, DateTime utcNow)
    {
        var reservation = await _reservations.GetAsync(reservationId);
        if (reservation is null || !IsOverdue(reservation, utcNow))
        {
            return (reservation, false);
        }

        // Rule 4: the status is re-checked under the lock, so release, confirm, lazy expiry and the sweeper
        // can't each restore the same quantity: whichever runs first moves it out of Active.
        var expired = reservation with { Status = ReservationStatus.Expired };
        await ReturnStockLockedAsync(reservation.ProductId, reservation.Quantity);
        await _reservations.UpdateAsync(reservationId, expired);
        return (expired, true);
    }

    private async Task ReturnStockLockedAsync(Guid productId, int quantity)
    {
        var product = await _products.GetAsync(productId)
            ?? throw new InvalidOperationException($"Product {productId} referenced by a reservation does not exist.");
        await _products.UpdateAsync(productId, product with { Stock = product.Stock + quantity });
    }

    private async Task<IDisposable> AcquireProductLockAsync(Guid productId, CancellationToken cancellationToken)
    {
        // Cancellation is honoured only while waiting for the lock, i.e. before any state has changed.
        var semaphore = _productLocks.GetOrAdd(productId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new LockReleaser(semaphore);
    }

    private sealed class LockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
