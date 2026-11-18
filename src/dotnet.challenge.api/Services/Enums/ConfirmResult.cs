// Namespace kept as dotnet.challenge.api.Services (see ReserveResult.cs).
namespace dotnet.challenge.api.Services;

public enum ConfirmResult
{
    Success,
    NotFound,
    AlreadyConfirmed,
    IllegalState
}
