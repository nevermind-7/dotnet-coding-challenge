using dotnet.challenge.api.Models;
using dotnet.challenge.api.Services;
using dotnet.challenge.data.Entities;
using Microsoft.AspNetCore.Mvc;

namespace dotnet.challenge.api.Controllers;

[ApiController]
[Route("api/reservations")]
[Produces("application/json")]
public class ReservationsController : ControllerBase
{
    private readonly IInventoryService _inventory;

    public ReservationsController(IInventoryService inventory)
    {
        _inventory = inventory;
    }

    [HttpPost]
    [ProducesResponseType(typeof(Reservation), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateReservation(
        [FromBody] CreateReservationRequest request, CancellationToken cancellationToken)
    {
        var (result, reservation) = await _inventory.TryReserveAsync(
            request.ProductId!.Value, request.Quantity, request.TtlSeconds, cancellationToken);

        return result switch
        {
            ReserveResult.Success => CreatedAtAction(nameof(GetReservation), new { id = reservation!.Id }, reservation),
            ReserveResult.ProductNotFound => NotFound(new { error = "Product not found." }),
            // Defect #3: was 200 OK, so clients that check the status code saw a failed reservation as a success.
            ReserveResult.InsufficientStock => Conflict(new { error = "Insufficient stock." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(Reservation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReservation(Guid id, CancellationToken cancellationToken)
    {
        var reservation = await _inventory.GetReservationAsync(id, cancellationToken);
        if (reservation is null)
        {
            return NotFound();
        }
        return Ok(reservation);
    }

    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType(typeof(Reservation), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken cancellationToken)
    {
        var result = await _inventory.ConfirmAsync(id, cancellationToken);
        return result switch
        {
            // Idempotent: a repeat confirm also returns 200, with the current reservation as the body.
            ConfirmResult.Success or ConfirmResult.AlreadyConfirmed =>
                Ok(await _inventory.GetReservationAsync(id, cancellationToken)),
            ConfirmResult.NotFound => NotFound(),
            ConfirmResult.IllegalState => Conflict(new { error = "Reservation is not in a confirmable state." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Release(Guid id, CancellationToken cancellationToken)
    {
        var result = await _inventory.ReleaseAsync(id, cancellationToken);
        return result switch
        {
            ReleaseResult.Success or ReleaseResult.AlreadyReleased => NoContent(),
            ReleaseResult.NotFound => NotFound(),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}
