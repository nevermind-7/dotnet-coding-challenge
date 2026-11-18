namespace dotnet.challenge.data.Entities;

/// <summary>
/// Immutable snapshot of a product. Changes are published by storing a new instance
/// (<c>product with { ... }</c>), so readers never observe an object being mutated.
/// </summary>
public sealed record Product
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Units currently available for new reservations.
    /// Decremented when a reservation is created, re-incremented when a reservation is released or expires.
    /// Must never be negative.
    /// </summary>
    public int Stock { get; init; }
}
