using dotnet.challenge.api.Services;
using dotnet.challenge.data.Cache;
using dotnet.challenge.data.Entities;
using Microsoft.Extensions.Time.Testing;

namespace dotnet.challenge.tests;

/// <summary>
/// Service-level tests for confirm / release / expiry (business rules 2-4).
/// Time is driven by <see cref="FakeTimeProvider"/> so expiry is deterministic and needs no sleeps.
/// </summary>
public class ReservationLifecycleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SimpleObjectCache<Guid, Reservation> _reservations = new();
    private readonly InventoryService _svc;

    public ReservationLifecycleTests()
    {
        _svc = new InventoryService(new SimpleObjectCache<Guid, Product>(), _reservations, _time);
    }

    private async Task<(Product Product, Reservation Reservation)> ReserveAsync(int stock, int quantity, int ttlSeconds = 60)
    {
        var product = await _svc.CreateProductAsync("Widget", stock);
        var (result, reservation) = await _svc.TryReserveAsync(product.Id, quantity, ttlSeconds);
        result.Should().Be(ReserveResult.Success);
        return (product, reservation!);
    }

    private async Task<int> StockOf(Guid productId) => (await _svc.GetProductAsync(productId))!.Stock;

    private async Task<ReservationStatus> StatusOf(Guid reservationId) =>
        (await _svc.GetReservationAsync(reservationId))!.Status;

    [Fact]
    public async Task Reserve_SetsUtcTimestampsFromTtl()
    {
        var (_, reservation) = await ReserveAsync(stock: 5, quantity: 1, ttlSeconds: 30);

        reservation.CreatedAt.Should().Be(_time.GetUtcNow().UtcDateTime);
        reservation.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        reservation.ExpiresAt.Should().Be(reservation.CreatedAt.AddSeconds(30));
    }

    [Fact]
    public async Task Confirm_Active_TransitionsToConfirmed_AndKeepsStockReserved()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2);

        (await _svc.ConfirmAsync(reservation.Id)).Should().Be(ConfirmResult.Success);

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Confirmed);
        (await StockOf(product.Id)).Should().Be(3);
    }

    [Fact]
    public async Task Confirm_IsIdempotent()
    {
        var (_, reservation) = await ReserveAsync(stock: 5, quantity: 2);
        await _svc.ConfirmAsync(reservation.Id);

        (await _svc.ConfirmAsync(reservation.Id)).Should().Be(ConfirmResult.AlreadyConfirmed);
        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Confirmed);
    }

    [Fact]
    public async Task Confirm_Unknown_ReturnsNotFound()
    {
        (await _svc.ConfirmAsync(Guid.NewGuid())).Should().Be(ConfirmResult.NotFound);
    }

    [Fact]
    public async Task Confirm_Released_IsIllegal()
    {
        var (_, reservation) = await ReserveAsync(stock: 5, quantity: 2);
        await _svc.ReleaseAsync(reservation.Id);

        (await _svc.ConfirmAsync(reservation.Id)).Should().Be(ConfirmResult.IllegalState);
        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Released);
    }

    [Fact]
    public async Task Confirmed_ReservationDoesNotExpire()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);
        await _svc.ConfirmAsync(reservation.Id);

        _time.Advance(TimeSpan.FromMinutes(5));
        (await _svc.SweepExpiredAsync()).Should().Be(0);

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Confirmed);
        (await StockOf(product.Id)).Should().Be(3);
    }

    [Fact]
    public async Task Release_Active_RestoresStock_AndIsIdempotent()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2);

        (await _svc.ReleaseAsync(reservation.Id)).Should().Be(ReleaseResult.Success);
        (await _svc.ReleaseAsync(reservation.Id)).Should().Be(ReleaseResult.AlreadyReleased);

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Released);
        (await StockOf(product.Id)).Should().Be(5, "stock is restored exactly once");
    }

    [Fact]
    public async Task Release_Confirmed_RestoresStock()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2);
        await _svc.ConfirmAsync(reservation.Id);

        (await _svc.ReleaseAsync(reservation.Id)).Should().Be(ReleaseResult.Success);

        (await StockOf(product.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Release_Unknown_ReturnsNotFound()
    {
        (await _svc.ReleaseAsync(Guid.NewGuid())).Should().Be(ReleaseResult.NotFound);
    }

    [Fact]
    public async Task Reservation_IsActive_UntilExpiryElapses()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);

        _time.Advance(TimeSpan.FromSeconds(9));

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Active);
        (await StockOf(product.Id)).Should().Be(3);
    }

    [Fact]
    public async Task Get_AfterExpiry_ObservesExpiredState_WithoutWaitingForSweep()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);

        _time.Advance(TimeSpan.FromSeconds(11));

        (await StockOf(product.Id)).Should().Be(5, "a product GET must see stock returned by expiry");
        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Expired);
        (await _svc.SweepExpiredAsync()).Should().Be(0, "already expired lazily, must not restore twice");
        (await StockOf(product.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Sweep_ExpiresOverdueReservations_AcrossProducts()
    {
        var (productA, reservationA) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);
        var (productB, reservationB) = await ReserveAsync(stock: 3, quantity: 3, ttlSeconds: 10);
        var (productC, reservationC) = await ReserveAsync(stock: 4, quantity: 1, ttlSeconds: 120);

        _time.Advance(TimeSpan.FromSeconds(30));

        (await _svc.SweepExpiredAsync()).Should().Be(2);
        (await _svc.SweepExpiredAsync()).Should().Be(0);

        (await StockOf(productA.Id)).Should().Be(5);
        (await StockOf(productB.Id)).Should().Be(3);
        (await StockOf(productC.Id)).Should().Be(3);
        (await StatusOf(reservationC.Id)).Should().Be(ReservationStatus.Active);
    }

    [Fact]
    public async Task Confirm_Expired_IsIllegal_AndRestoresStockOnce()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);
        _time.Advance(TimeSpan.FromSeconds(11));

        (await _svc.ConfirmAsync(reservation.Id)).Should().Be(ConfirmResult.IllegalState);

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Expired);
        (await StockOf(product.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Release_Expired_IsNoOp_AndDoesNotRestoreTwice()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);
        _time.Advance(TimeSpan.FromSeconds(11));
        await _svc.SweepExpiredAsync();

        (await _svc.ReleaseAsync(reservation.Id)).Should().Be(ReleaseResult.AlreadyReleased);

        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Expired);
        (await StockOf(product.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Reserve_ReclaimsStockHeldByOverdueReservations()
    {
        var (product, _) = await ReserveAsync(stock: 2, quantity: 2, ttlSeconds: 10);
        _time.Advance(TimeSpan.FromSeconds(11));

        var (result, _) = await _svc.TryReserveAsync(product.Id, 2, 60);

        result.Should().Be(ReserveResult.Success);
        (await StockOf(product.Id)).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentReleaseConfirmAndSweep_OfSameReservation_RestoreStockExactlyOnce()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var (product, reservation) = await ReserveAsync(stock: 5, quantity: 3, ttlSeconds: 10);
            _time.Advance(TimeSpan.FromSeconds(11));

            var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
            {
                switch (i % 3)
                {
                    case 0: await _svc.ReleaseAsync(reservation.Id); break;
                    case 1: await _svc.ConfirmAsync(reservation.Id); break;
                    default: await _svc.SweepExpiredAsync(); break;
                }
            }));
            await Task.WhenAll(tasks);

            (await StockOf(product.Id)).Should().Be(5, $"iteration {iteration}");
            (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Expired);
        }
    }

    [Fact]
    public async Task MixedConcurrentOperations_PreserveStockInvariant()
    {
        const int initialStock = 20;
        var product = await _svc.CreateProductAsync("Widget", initialStock);

        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var (result, reservation) = await _svc.TryReserveAsync(product.Id, 1 + i % 3, 60);
            if (result != ReserveResult.Success)
            {
                return;
            }
            switch (i % 4)
            {
                case 0: await _svc.ConfirmAsync(reservation!.Id); break;
                case 1: await _svc.ReleaseAsync(reservation!.Id); break;
                case 2:
                    await _svc.ConfirmAsync(reservation!.Id);
                    await _svc.ReleaseAsync(reservation.Id);
                    break;
                    // case 3: leave Active
            }
        }));
        await Task.WhenAll(tasks);

        var stock = await StockOf(product.Id);
        var held = (await AllReservationsOf(product.Id))
            .Where(r => r.Status is ReservationStatus.Active or ReservationStatus.Confirmed)
            .Sum(r => r.Quantity);

        stock.Should().BeGreaterThanOrEqualTo(0);
        (stock + held).Should().Be(initialStock, "available + held must always equal the initial stock");
    }

    // Rule 1 under contention on the reclaim path: the only stock available is held by overdue
    // reservations, and concurrent reservers race with concurrent sweeps to claim it.
    [Fact]
    public async Task ConcurrentReserves_ReclaimingOverdueStock_NeverOversell()
    {
        const int initialStock = 10;

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var product = await _svc.CreateProductAsync("Widget", initialStock);
            for (var i = 0; i < initialStock; i++)
            {
                await _svc.TryReserveAsync(product.Id, 1, ttlSeconds: 10);
            }
            _time.Advance(TimeSpan.FromSeconds(11));

            var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            {
                if (i % 5 == 0)
                {
                    await _svc.SweepExpiredAsync();
                    return ReserveResult.ProductNotFound; // not a reservation attempt
                }
                return (await _svc.TryReserveAsync(product.Id, 1, ttlSeconds: 60)).Result;
            }));
            var results = await Task.WhenAll(tasks);

            results.Count(r => r == ReserveResult.Success).Should().Be(initialStock, $"iteration {iteration}");
            (await StockOf(product.Id)).Should().Be(0, $"iteration {iteration}");
            (await HeldQuantity(product.Id)).Should().Be(initialStock, $"iteration {iteration}");
        }
    }

    // Rule 4: release first, then let the expiry time pass. Expiry must not restore the stock again.
    [Fact]
    public async Task Release_ThenExpiryElapses_DoesNotRestoreAgain()
    {
        var (product, reservation) = await ReserveAsync(stock: 5, quantity: 2, ttlSeconds: 10);
        await _svc.ReleaseAsync(reservation.Id);

        _time.Advance(TimeSpan.FromSeconds(11));

        (await _svc.SweepExpiredAsync()).Should().Be(0);
        (await StatusOf(reservation.Id)).Should().Be(ReservationStatus.Released);
        (await StockOf(product.Id)).Should().Be(5);
    }

    // Rule 4: several reservations with different quantities, ended by release, expiry and
    // confirm + release in mixed order. Each one restores exactly its own quantity.
    [Fact]
    public async Task MixedQuantities_EndedInAnyOrder_RestoreExactlyTheReservedAmounts()
    {
        var product = await _svc.CreateProductAsync("Widget", 10);
        var r1 = (await _svc.TryReserveAsync(product.Id, 1, ttlSeconds: 10)).Reservation!;
        var r2 = (await _svc.TryReserveAsync(product.Id, 2, ttlSeconds: 60)).Reservation!;
        var r3 = (await _svc.TryReserveAsync(product.Id, 3, ttlSeconds: 10)).Reservation!;
        var r4 = (await _svc.TryReserveAsync(product.Id, 4, ttlSeconds: 60)).Reservation!;
        (await StockOf(product.Id)).Should().Be(0);

        await _svc.ReleaseAsync(r4.Id);
        (await StockOf(product.Id)).Should().Be(4);

        _time.Advance(TimeSpan.FromSeconds(11)); // r1 and r3 become overdue
        (await _svc.ReleaseAsync(r3.Id)).Should().Be(ReleaseResult.AlreadyReleased);
        (await StockOf(product.Id)).Should().Be(8, "r3 (3) and r1 (1) are returned by expiry, once each");

        await _svc.ConfirmAsync(r2.Id);
        (await StockOf(product.Id)).Should().Be(8, "confirming keeps the stock reserved");
        await _svc.ReleaseAsync(r2.Id);
        await _svc.ReleaseAsync(r1.Id);
        await _svc.SweepExpiredAsync();

        (await StockOf(product.Id)).Should().Be(10);
        (await HeldQuantity(product.Id)).Should().Be(0);
    }

    private async Task<int> HeldQuantity(Guid productId) =>
        (await AllReservationsOf(productId))
            .Where(r => r.Status is ReservationStatus.Active or ReservationStatus.Confirmed)
            .Sum(r => r.Quantity);

    private async Task<List<Reservation>> AllReservationsOf(Guid productId) =>
        (await _reservations.GetAllAsync()).Where(r => r.ProductId == productId).ToList();
}
