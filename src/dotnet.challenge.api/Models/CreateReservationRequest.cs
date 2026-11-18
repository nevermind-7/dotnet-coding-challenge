using System.ComponentModel.DataAnnotations;

namespace dotnet.challenge.api.Models;

public class CreateReservationRequest
{
    // Nullable so that [Required] can actually detect a missing value: on a non-nullable Guid
    // the binder supplies Guid.Empty and [Required] always passes.
    [Required]
    public Guid? ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }

    [Range(1, 3600)]
    public int TtlSeconds { get; set; }
}
