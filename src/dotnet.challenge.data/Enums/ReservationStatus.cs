// The folder groups the file, but the namespace stays dotnet.challenge.data.Entities: the provided
// acceptance tests reference ReservationStatus through that namespace and must not be modified.
namespace dotnet.challenge.data.Entities;

public enum ReservationStatus
{
    Active,
    Confirmed,
    Released,
    Expired
}
