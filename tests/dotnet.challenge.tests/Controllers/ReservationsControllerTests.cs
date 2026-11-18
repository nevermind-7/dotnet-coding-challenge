using dotnet.challenge.api.Controllers;
using dotnet.challenge.api.Models;
using dotnet.challenge.api.Services;
using dotnet.challenge.data.Entities;
using dotnet.challenge.tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace dotnet.challenge.tests.Controllers;

/// <summary>
/// Unit tests for <see cref="ReservationsController"/>: every service outcome maps to the expected
/// HTTP result. Model validation (400) is covered by ApiTests, since it runs in the MVC pipeline.
/// </summary>
public class ReservationsControllerTests
{
    private readonly FakeInventoryService _inventory = new();
    private readonly ReservationsController _controller;
    private readonly Reservation _reservation = new()
    {
        Id = Guid.NewGuid(),
        ProductId = Guid.NewGuid(),
        Quantity = 2,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddSeconds(60),
        Status = ReservationStatus.Active
    };

    public ReservationsControllerTests()
    {
        _controller = new ReservationsController(_inventory);
    }

    private static CreateReservationRequest Request(Guid productId) =>
        new() { ProductId = productId, Quantity = 2, TtlSeconds = 60 };

    // ---- POST /api/reservations ----

    [Fact]
    public async Task CreateReservation_Success_Returns201_PointingAtGetReservation()
    {
        _inventory.ReserveResult = ReserveResult.Success;
        _inventory.Reservation = _reservation;
        using var cts = new CancellationTokenSource();

        var result = await _controller.CreateReservation(Request(_reservation.ProductId), cts.Token);

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.ActionName.Should().Be(nameof(ReservationsController.GetReservation));
        created.RouteValues!["id"].Should().Be(_reservation.Id);
        created.Value.Should().Be(_reservation);

        _inventory.ReservedWith.Should().Be((_reservation.ProductId, 2, 60));
        _inventory.LastToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task CreateReservation_ProductNotFound_Returns404()
    {
        _inventory.ReserveResult = ReserveResult.ProductNotFound;

        var result = await _controller.CreateReservation(Request(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CreateReservation_InsufficientStock_Returns409()
    {
        _inventory.ReserveResult = ReserveResult.InsufficientStock;

        var result = await _controller.CreateReservation(Request(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ---- GET /api/reservations/{id} ----

    [Fact]
    public async Task GetReservation_Existing_Returns200WithReservation()
    {
        _inventory.Reservation = _reservation;

        var result = await _controller.GetReservation(_reservation.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(_reservation);
        _inventory.LastId.Should().Be(_reservation.Id);
    }

    [Fact]
    public async Task GetReservation_Unknown_Returns404()
    {
        _inventory.Reservation = null;

        var result = await _controller.GetReservation(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- POST /api/reservations/{id}/confirm ----

    [Theory]
    [InlineData(ConfirmResult.Success)]
    [InlineData(ConfirmResult.AlreadyConfirmed)]
    public async Task Confirm_SuccessOrAlreadyConfirmed_Returns200WithReservation(ConfirmResult outcome)
    {
        var confirmed = _reservation with { Status = ReservationStatus.Confirmed };
        _inventory.ConfirmResult = outcome;
        _inventory.Reservation = confirmed;

        var result = await _controller.Confirm(_reservation.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().Be(confirmed);
    }

    [Fact]
    public async Task Confirm_Unknown_Returns404()
    {
        _inventory.ConfirmResult = ConfirmResult.NotFound;

        var result = await _controller.Confirm(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Confirm_IllegalState_Returns409()
    {
        _inventory.ConfirmResult = ConfirmResult.IllegalState;

        var result = await _controller.Confirm(_reservation.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Confirm_ForwardsIdAndCancellationToken()
    {
        _inventory.ConfirmResult = ConfirmResult.NotFound;
        using var cts = new CancellationTokenSource();

        await _controller.Confirm(_reservation.Id, cts.Token);

        _inventory.LastId.Should().Be(_reservation.Id);
        _inventory.LastToken.Should().Be(cts.Token);
    }

    // ---- DELETE /api/reservations/{id} ----

    [Theory]
    [InlineData(ReleaseResult.Success)]
    [InlineData(ReleaseResult.AlreadyReleased)]
    public async Task Release_SuccessOrAlreadyReleased_Returns204(ReleaseResult outcome)
    {
        _inventory.ReleaseResult = outcome;

        var result = await _controller.Release(_reservation.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Release_Unknown_Returns404()
    {
        _inventory.ReleaseResult = ReleaseResult.NotFound;

        var result = await _controller.Release(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Release_ForwardsIdAndCancellationToken()
    {
        _inventory.ReleaseResult = ReleaseResult.Success;
        using var cts = new CancellationTokenSource();

        await _controller.Release(_reservation.Id, cts.Token);

        _inventory.LastId.Should().Be(_reservation.Id);
        _inventory.LastToken.Should().Be(cts.Token);
    }
}
