using dotnet.challenge.api.Services;

namespace dotnet.challenge.api.Background;

/// <summary>
/// Periodically sweeps Active reservations whose ExpiresAt has passed and returns
/// their quantity to the owning product's Stock.
/// <para>
/// Reads also expire overdue reservations lazily, so correctness does not depend on this
/// sweep's timing; the sweep only makes sure expiry happens even when nobody reads.
/// </para>
/// </summary>
// Defect #5: the original ran `while (true)` + Thread.Sleep inside Task.Run, which blocked a thread-pool
// thread forever, used sync-over-async (GetResult), ignored cancellation and had a no-op StopAsync.
// BackgroundService + PeriodicTimer awaits instead of blocking and stops on the host's stoppingToken.
public sealed class ReservationExpirySweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReservationExpirySweeper> _logger;

    public ReservationExpirySweeper(IServiceScopeFactory scopeFactory, ILogger<ReservationExpirySweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                    var expired = await inventory.SweepExpiredAsync(stoppingToken);
                    if (expired > 0)
                    {
                        _logger.LogInformation("Expired {Count} reservation(s)", expired);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Expiry sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }
}
