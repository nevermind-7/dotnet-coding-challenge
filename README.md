# .NET Coding Challenge — Inventory Reservations

This is a .NET coding challenge. We assume you are comfortable with .NET Core, ASP.NET Core, and REST APIs.

You will extend a small **inventory reservation** service. The scaffold is intentionally incomplete and **contains subtle defects**. We are as interested in what you *find* in the provided code as in what you *write* yourself.

We would like you to spend no more than **3 hours** on this.

## Ground rules

1. Use .NET 8 and C#
2. The API consumes and returns JSON
3. You do not need to consider authentication, authorization, or HTTPS
4. No persistent storage — use the provided in-memory cache
5. You may add any NuGet package you wish, but be prepared to justify it
6. **Do not disable, weaken, or modify the provided acceptance tests.** You may add tests of your own.

## The domain

The service tracks **products** (each with some available stock) and lets clients **reserve** stock for a short window before either confirming the reservation (finalising a sale) or releasing it (returning the stock). Reservations that are neither confirmed nor released before their expiry time are automatically expired and the stock is returned to the product.

### Entities

**Product**
| Field | Type | Notes |
|--|--|--|
| `Id` | `Guid` | Server-assigned |
| `Name` | `string` | Required, max length 128 |
| `Stock` | `int` | Currently available units. Must never go negative. |

**Reservation**
| Field | Type | Notes |
|--|--|--|
| `Id` | `Guid` | Server-assigned |
| `ProductId` | `Guid` | Required, must refer to an existing product |
| `Quantity` | `int` | Required, must be `>= 1` |
| `CreatedAt` | `DateTime` | UTC, server-assigned |
| `ExpiresAt` | `DateTime` | UTC, server-assigned (`CreatedAt + TTL`) |
| `Status` | `string` | One of `Active`, `Confirmed`, `Released`, `Expired` |

### Endpoints you will need

```
POST   /api/products                       create a product
GET    /api/products/{id}                  get a product (includes current Stock)

POST   /api/reservations                   create a reservation
       body: { productId, quantity, ttlSeconds }
POST   /api/reservations/{id}/confirm      confirm (idempotent)
DELETE /api/reservations/{id}              release (idempotent, returns stock)
GET    /api/reservations/{id}              get a reservation (includes current Status)
```

Some of these are partially implemented in the scaffold. Complete the rest.

### Business rules

1. **No overselling.** The sum of `Active` + `Confirmed` reservation quantities for a product must never exceed the stock that existed when the product was created. Under concurrent load the service must honour this invariant exactly.
2. **Expiry.** A reservation in state `Active` whose `ExpiresAt` is in the past is considered `Expired` and its quantity is returned to the product's `Stock`. The transition happens automatically; clients must observe the correct state and stock on a `GET` issued after expiry has elapsed.
3. **Idempotency.**
   - Confirming an already-`Confirmed` reservation is a no-op and returns 200.
   - Releasing an already-`Released` (or `Expired`) reservation is a no-op and returns 204.
   - Confirming an `Expired` or `Released` reservation must fail with an appropriate error status.
4. **Stock arithmetic.** Releasing or expiring a reservation restores exactly the reserved quantity, no more and no less, regardless of ordering.

### HTTP status codes

Be deliberate — we will be reading the controller. At minimum:

| Situation | Expected |
|--|--|
| Resource created | `201 Created` with `Location` header |
| Resource retrieved | `200 OK` |
| Resource removed / idempotent release | `204 No Content` |
| Body fails schema validation | `400 Bad Request` |
| Unknown id | `404 Not Found` |
| Insufficient stock / illegal state transition | `409 Conflict` |

## Your task

1. **Read the scaffold carefully.** It compiles and a basic test suite passes. It is not, however, correct. The scaffold contains **five defects** — some functional, some architectural, some subtle. Find them. Fix them. Write one sentence per defect in your submission explaining what it was and why your fix is correct.

2. **Implement the missing functionality** needed to satisfy the spec above (confirm, release, expiry, anything else that is stubbed or absent).

3. **Make `ConcurrencyTests` pass.** The provided `tests/dotnet.challenge.tests/ConcurrencyTests.cs` is the acceptance test for rule 1 (no overselling). It is currently failing. You may not modify it.

4. Ensure the rest of the test suite still passes and add any tests of your own that you think are valuable.

## How we will assess your work

- **Correctness under load.** Does `ConcurrencyTests` pass reliably (not just once)?
- **Root-cause thinking.** Did you identify the actual causes of the defects, or patch symptoms? Your per-defect write-up is how we judge this.
- **Production quality.** Readable, SOLID, testable, correct HTTP semantics, disciplined use of `async`/cancellation, no obvious footguns.
- **Restraint.** The scaffold is small on purpose. Don't rewrite it; extend it.

When you're ready to share your work, push to a private repo and add your reviewer as a collaborator.

---

## Submission notes

### Defects found in the scaffold

1. **`SimpleObjectCache` was backed by a plain `Dictionary`** — concurrent writers corrupt its internal buckets (lost entries, or infinite loops that show up as the "timed out" path in `ConcurrencyTests`); it now uses a `ConcurrentDictionary`, and `UpdateAsync` replaces its `ContainsKey`-then-write race with an atomic `TryUpdate` loop so a concurrently deleted key can't come back.
2. **`TryReserveAsync` was an unsynchronised check-then-act** (read stock → compare → write back), so two requests could both see `Stock = 1` and both succeed (the test saw 11 successes out of 10 units). A thread-safe cache alone can't fix this because the atomic unit spans several cache calls, so every read-modify-write on a product and its reservations now runs under a per-product `SemaphoreSlim` (async-friendly, and products don't contend with each other). Entities became immutable records published copy-on-write, so readers outside the lock never see an object halfway through a change.
3. **Insufficient stock returned `200 OK`** with an error body, so clients reading the status code would treat a failed reservation as a success; it now returns `409 Conflict` as the spec requires.
4. **`IsExpired` compared the UTC `ExpiresAt` with local `DateTime.Now`**, so on a UTC−3 server reservations lived three extra hours (and on UTC+ servers they expired immediately). Expiry now compares against `TimeProvider.GetUtcNow()`, which is injected so tests can drive it deterministically.
5. **The expiry sweeper blocked a thread-pool thread forever** (`Task.Run` + `while (true)` + `Thread.Sleep` + sync-over-async `GetResult()`), ignored cancellation, and had a no-op `StopAsync`, so it couldn't shut down cleanly. It's now a `BackgroundService` using `PeriodicTimer` and the host's `stoppingToken`.

Smaller issues fixed along the way:
- `[Required]` on a non-nullable `Guid ProductId` never failed (a missing value binds as `Guid.Empty` and returned 404). It's now `Guid?`, so a missing id gives `400`.
- `Status` was serialised as a number; `JsonStringEnumConverter` makes it the string the spec describes.
- `global.json` was relaxed from `8.0.410` to `8.0.400` (still `latestFeature`), so any 8.0.4xx SDK can build it.

### Implementation notes

- **Confirm / release / expiry** run under the product lock and re-read the reservation inside it, so each transition happens once and stock is restored exactly once whatever order things happen in (rule 4).
- **Expiry is applied lazily on reads**: `GET` on a reservation or product, confirm, release, and a reserve that would otherwise fail for lack of stock all expire overdue reservations first. A client therefore sees the right state and stock as soon as `ExpiresAt` has passed, without waiting for the 1 s sweep (rule 2). The sweeper only makes sure expiry also happens when nobody reads.
- `CancellationToken` goes from the controllers down to the service and is honoured only while waiting for the lock, i.e. before any state has changed, so a cancelled request never leaves a half-applied transition.
- Trade-offs that are fine at this scale: locks are in-process (single instance only), the lock map grows with the number of products, and finding a product's overdue reservations scans the reservation cache. A production version would back this with a store that has atomic conditional updates and an index by product.

### Added NuGet packages (test project only)

- `Microsoft.AspNetCore.Mvc.Testing` — runs the real pipeline in memory to check status codes, `Location` headers, validation and JSON shape (`ApiTests`).
- `Microsoft.Extensions.TimeProvider.Testing` — `FakeTimeProvider` makes the expiry tests deterministic, with no `Thread.Sleep`.

### Added tests

- `ReservationLifecycleTests` — confirm/release idempotency, illegal transitions, lazy and swept expiry, stock reclaimed from overdue reservations, concurrent release/confirm/sweep on the same reservation, and a mixed-load check of the invariant `available + held == initial`.
- `Services/InventoryServiceTests` — unit tests for argument guards, cancellation (honoured before any state change), boundaries (exact stock, expiry at exactly `ExpiresAt`), immutable snapshots and isolation between products.
- `Controllers/ProductsControllerTests`, `Controllers/ReservationsControllerTests` — unit tests that map every service outcome to its `IActionResult` and check that ids and `CancellationToken` are forwarded. They use a hand-written `Fakes/FakeInventoryService`, so no mocking library is needed.
- `ApiTests` — HTTP semantics for every endpoint (201 + `Location`, 200, 204, 400, 404, 409), expiry seen through `GET`, and the hosted sweeper expiring reservations with no client reads.

### Change log

File-by-file summary of every change made to the scaffold.

**Configuration**

**`dotnet.challenge.data`**
- `Cache/SimpleObjectCache.cs` — `Dictionary` → `ConcurrentDictionary`; `UpdateAsync` made atomic (`TryUpdate` loop) and `DeleteAsync` uses `TryRemove`. *(Defect #1)*
- `Entities/Product.cs`, `Entities/Reservation.cs` — `class` with setters → `sealed record` with `init`, so changes are published copy-on-write and readers always see a consistent snapshot. *(Defect #2)*
- `Enums/ReservationStatus.cs` — *new file*: the enum moved out of `Reservation.cs`, keeping namespace `dotnet.challenge.data.Entities`.

**`dotnet.challenge.api`**
- `Services/InventoryService.cs`
  - Per-product `SemaphoreSlim` around every read-modify-write. *(Defect #2)*
  - Expiry uses an injected `TimeProvider` in UTC instead of `DateTime.Now`. *(Defect #4)*
  - Implemented `ConfirmAsync`, `ReleaseAsync` and `SweepExpiredAsync`.
  - Lazy expiry on reads, confirm, release and reserve.
  - `CancellationToken` support and argument guards.
- `Services/Interfaces/IInventoryService.cs` — *moved* from `Services/`. Methods take an optional `CancellationToken`, and `SweepExpiredAsync` returns the number of reservations it expired.
- `Services/Enums/ReserveResult.cs`, `ConfirmResult.cs`, `ReleaseResult.cs` — *new files*: enums moved out of the interface file, keeping namespace `dotnet.challenge.api.Services`.
- `Controllers/ReservationsController.cs`
  - Insufficient stock returns `409 Conflict` instead of `200 OK`. *(Defect #3)*
  - Confirm returns the reservation as the body.
  - `CancellationToken` is forwarded to the service, and `[ProducesResponseType]` attributes were added.
- `Controllers/ProductsController.cs` — `CancellationToken` forwarding and `[ProducesResponseType]` attributes.
- `Background/ReservationExpirySweeper.cs` — rewritten from an `IHostedService` with a blocking `Thread.Sleep` loop to a `BackgroundService` with `PeriodicTimer` and `stoppingToken`. *(Defect #5)*
- `Models/CreateReservationRequest.cs` — `ProductId` is now `Guid?`, so `[Required]` rejects a missing value with `400`.
- `Startup.cs` — `JsonStringEnumConverter` so `Status` serialises as a string, and `TimeProvider.System` registered in DI.

**`dotnet.challenge.tests`**
- `BasicTests.cs`, `ConcurrencyTests.cs` — **unchanged** (provided acceptance tests).
- `dotnet.challenge.tests.csproj` — added `Microsoft.AspNetCore.Mvc.Testing` and `Microsoft.Extensions.TimeProvider.Testing`.
- New: `ReservationLifecycleTests.cs`, `Services/InventoryServiceTests.cs`, `Controllers/ProductsControllerTests.cs`, `Controllers/ReservationsControllerTests.cs`, `Fakes/FakeInventoryService.cs`, `ApiTests.cs`.

**Docs**
- `README.md` — added this *Submission notes* section.
