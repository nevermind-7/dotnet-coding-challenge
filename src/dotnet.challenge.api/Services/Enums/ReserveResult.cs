// The folder groups the file, but the namespace stays dotnet.challenge.api.Services: it is the public
// contract the provided acceptance tests compile against, and they must not be modified.
namespace dotnet.challenge.api.Services;

public enum ReserveResult
{
    Success,
    ProductNotFound,
    InsufficientStock
}
