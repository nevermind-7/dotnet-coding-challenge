using dotnet.challenge.api.Services;
using dotnet.challenge.data.Cache;
using dotnet.challenge.data.Entities;
using Microsoft.Extensions.Time.Testing;

namespace dotnet.challenge.tests.Services;

/// <summary>
/// Unit tests for <see cref="InventoryService"/> covering what ReservationLifecycleTests doesn't:
/// argument guards, cancellation, boundaries, snapshot immutability and isolation between products.
/// The real SimpleObjectCache is used as the store: it is already an in-memory fake, so mocking it
/// would only add noise.
/// </summary>
public class InventoryServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InventoryService _svc;

    public InventoryServiceTests()
    {
        _svc = new InventoryService(
            new SimpleObjectCache<Guid, Product>(),
            new SimpleObjectCache<Guid, Reservation>(),
            _time);
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    // ---- Argument guards ----

    [Fact]
    public async Task CreateProduct_NegativeStock_Throws()
    {
        var act = () => _svc.CreateProductAsync("Widget", -1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task TryReserve_NonPositiveQuantity_Throws(int quantity)
    {
        var product = await _svc.CreateProductAsync("Widget", 5);

        var act = () => _svc.TryReserveAsync(product.Id, quantity, 60);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task TryReserve_NonPositiveTtl_Throws(int ttlSeconds)
    {
        var product = await _svc.CreateProductAsync("Widget", 5);

        var act = () => _svc.TryReserveAsync(product.Id, 1, ttlSeconds);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Boundaries ----

    [Fact]
    public async Task CreateProduct_ZeroStock_IsAllowed_ButCannotBeReserved()
    {
        var product = await _svc.CreateProductAsync("Widget", 0);

        var (result, _) = await _svc.TryReserveAsync(product.Id, 1, 60);

        result.Should().Be(ReserveResult.InsufficientStock);
    }

    [Fact]
    public async Task TryReserve_ExactlyTheRemainingStock_Succeeds_AndLeavesZero()
    {
        var product = await _svc.CreateProductAsync("Widget", 3);

        var (result, _) = await _svc.TryReserveAsync(product.Id, 3, 60);

        result.Should().Be(ReserveResult.Success);
        (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(0);
    }

    [Fact]
    public async Task Reservation_ExpiresExactlyAtExpiresAt()
    {
        var product = await _svc.CreateProductAsync("Widget", 3);
        var (_, reservation) = await _svc.TryReserveAsync(product.Id, 1, ttlSeconds: 10);

        _time.Advance(TimeSpan.FromSeconds(10));

        (await _svc.GetReservationAsync(reservation!.Id))!.Status.Should().Be(ReservationStatus.Expired);
    }

    [Fact]
    public async Task GetReservation_Unknown_ReturnsNull()
    {
        (await _svc.GetReservationAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Sweep_WithNothingOverdue_ReturnsZero_AndChangesNothing()
    {
        var product = await _svc.CreateProductAsync("Widget", 3);
        await _svc.TryReserveAsync(product.Id, 1, ttlSeconds: 60);

        (await _svc.SweepExpiredAsync()).Should().Be(0);
        (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(2);
    }

    // ---- Cancellation: honoured before any state change, never half-way through ----

    [Fact]
    public async Task TryReserve_CancelledToken_Throws_AndLeavesStockUntouched()
    {
        var product = await _svc.CreateProductAsync("Widget", 5);

        var act = () => _svc.TryReserveAsync(product.Id, 2, 60, Cancelled());

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(5);
    }

    [Fact]
    public async Task Confirm_CancelledToken_Throws_AndReservationStaysActive()
    {
        var product = await _svc.CreateProductAsync("Widget", 5);
        var (_, reservation) = await _svc.TryReserveAsync(product.Id, 2, 60);

        var act = () => _svc.ConfirmAsync(reservation!.Id, Cancelled());

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _svc.GetReservationAsync(reservation!.Id))!.Status.Should().Be(ReservationStatus.Active);
    }

    [Fact]
    public async Task Release_CancelledToken_Throws_AndStockIsNotRestored()
    {
        var product = await _svc.CreateProductAsync("Widget", 5);
        var (_, reservation) = await _svc.TryReserveAsync(product.Id, 2, 60);

        var act = () => _svc.ReleaseAsync(reservation!.Id, Cancelled());

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(3);
        (await _svc.GetReservationAsync(reservation!.Id))!.Status.Should().Be(ReservationStatus.Active);
    }

    [Fact]
    public async Task Sweep_CancelledToken_Throws_AndNothingIsExpired()
    {
        var product = await _svc.CreateProductAsync("Widget", 5);
        await _svc.TryReserveAsync(product.Id, 2, ttlSeconds: 10);
        _time.Advance(TimeSpan.FromSeconds(11));

        var act = () => _svc.SweepExpiredAsync(Cancelled());

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await _svc.SweepExpiredAsync()).Should().Be(1, "the cancelled sweep must not have expired it");
    }

    // ---- Snapshots: returned entities never change under the caller ----

    [Fact]
    public async Task ReturnedEntities_AreSnapshots_NotLiveObjects()
    {
        var product = await _svc.CreateProductAsync("Widget", 5);
        var (_, reservation) = await _svc.TryReserveAsync(product.Id, 2, 60);
        await _svc.ConfirmAsync(reservation!.Id);

        product.Stock.Should().Be(5, "the instance returned on create is not mutated by later reservations");
        reservation.Status.Should().Be(ReservationStatus.Active, "the instance returned on reserve is not mutated by confirm");

        (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(3);
        (await _svc.GetReservationAsync(reservation.Id))!.Status.Should().Be(ReservationStatus.Confirmed);
    }

    // ---- Isolation between products ----

    [Fact]
    public async Task Operations_OnOneProduct_DoNotAffectAnother()
    {
        var a = await _svc.CreateProductAsync("A", 5);
        var b = await _svc.CreateProductAsync("B", 5);

        var (_, reservation) = await _svc.TryReserveAsync(a.Id, 4, ttlSeconds: 10);
        (await _svc.GetProductAsync(b.Id))!.Stock.Should().Be(5);

        _time.Advance(TimeSpan.FromSeconds(11));
        await _svc.SweepExpiredAsync();

        (await _svc.GetProductAsync(a.Id))!.Stock.Should().Be(5);
        (await _svc.GetProductAsync(b.Id))!.Stock.Should().Be(5);
        (await _svc.GetReservationAsync(reservation!.Id))!.ProductId.Should().Be(a.Id);
    }

    [Fact]
    public async Task ConcurrentReservations_OnDifferentProducts_EachHonourTheirOwnStock()
    {
        var products = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _svc.CreateProductAsync($"P{i}", 4)));

        var results = await Task.WhenAll(products.SelectMany(p => Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () => (p.Id, (await _svc.TryReserveAsync(p.Id, 1, 60)).Result)))));

        foreach (var product in products)
        {
            results.Count(r => r.Id == product.Id && r.Result == ReserveResult.Success).Should().Be(4);
            (await _svc.GetProductAsync(product.Id))!.Stock.Should().Be(0);
        }
    }
}
