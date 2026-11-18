using dotnet.challenge.data.Entities;

// Namespace kept as dotnet.challenge.api.Services (see Enums/ReserveResult.cs).
namespace dotnet.challenge.api.Services;

// Every method takes an optional CancellationToken: the controllers pass RequestAborted and the
// sweeper passes the host's stoppingToken. It is optional so existing callers keep compiling.
public interface IInventoryService
{
    Task<Product> CreateProductAsync(string name, int stock, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the product with any overdue reservations already expired, so <see cref="Product.Stock"/>
    /// reflects stock returned by expiry even if the background sweep has not run yet.
    /// </summary>
    Task<Product?> GetProductAsync(Guid id, CancellationToken cancellationToken = default);

    Task<(ReserveResult Result, Reservation? Reservation)> TryReserveAsync(
        Guid productId, int quantity, int ttlSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the reservation, expiring it first if it is Active and past its ExpiresAt.
    /// </summary>
    Task<Reservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConfirmResult> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken = default);

    Task<ReleaseResult> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expires every overdue Active reservation and returns its quantity to stock.
    /// </summary>
    /// <returns>The number of reservations expired, so the sweeper can log real activity.</returns>
    Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default);
}
