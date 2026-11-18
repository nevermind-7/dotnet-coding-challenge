using dotnet.challenge.api.Services;
using dotnet.challenge.data.Entities;

namespace dotnet.challenge.tests.Fakes;

/// <summary>
/// Hand-written test double for <see cref="IInventoryService"/>, so controller unit tests run without
/// the real service and without a mocking library. Each method returns the configured result and
/// records the arguments it received.
/// </summary>
internal sealed class FakeInventoryService : IInventoryService
{
    // Configured responses
    public Product? Product { get; set; }
    public Reservation? Reservation { get; set; }
    public ReserveResult ReserveResult { get; set; }
    public ConfirmResult ConfirmResult { get; set; }
    public ReleaseResult ReleaseResult { get; set; }

    // Recorded calls
    public (string Name, int Stock)? CreatedWith { get; private set; }
    public (Guid ProductId, int Quantity, int TtlSeconds)? ReservedWith { get; private set; }
    public Guid? LastId { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public Task<Product> CreateProductAsync(string name, int stock, CancellationToken cancellationToken = default)
    {
        CreatedWith = (name, stock);
        LastToken = cancellationToken;
        return Task.FromResult(Product!);
    }

    public Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken = default)
    {
        LastId = id;
        LastToken = cancellationToken;
        return Task.FromResult(Product);
    }

    public Task<(ReserveResult Result, Reservation? Reservation)> TryReserveAsync(
        Guid productId, int quantity, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        ReservedWith = (productId, quantity, ttlSeconds);
        LastToken = cancellationToken;
        return Task.FromResult((ReserveResult, ReserveResult == ReserveResult.Success ? Reservation : null));
    }

    public Task<Reservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        LastId = id;
        LastToken = cancellationToken;
        return Task.FromResult(Reservation);
    }

    public Task<ConfirmResult> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        LastId = reservationId;
        LastToken = cancellationToken;
        return Task.FromResult(ConfirmResult);
    }

    public Task<ReleaseResult> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        LastId = reservationId;
        LastToken = cancellationToken;
        return Task.FromResult(ReleaseResult);
    }

    public Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
