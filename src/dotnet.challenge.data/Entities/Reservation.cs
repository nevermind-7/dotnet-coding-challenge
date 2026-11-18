namespace dotnet.challenge.data.Entities;

/// <summary>
/// Immutable snapshot of a reservation. State transitions store a new instance.
/// </summary>
public sealed record Reservation
{
    public Guid Id { get; init; }

    public Guid ProductId { get; init; }

    public int Quantity { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime ExpiresAt { get; init; }

    public ReservationStatus Status { get; init; }
}
