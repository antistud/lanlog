using Logrr.Notify;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Server.Hosting;

/// <summary>Runs the webhook delivery dispatcher for the app's lifetime (SPEC §10.5).</summary>
public sealed class DispatcherService(DeliveryDispatcher dispatcher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => dispatcher.RunAsync(stoppingToken);
}

/// <summary>
/// Hourly retention + disk guard, plus occurrence pruning (SPEC §4.6, §10.4). Runs once at
/// startup, then every hour.
/// </summary>
public sealed class RetentionService(
    RetentionMaintenance maintenance, AppStore apps, OccurrenceStore occurrences,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var result = maintenance.Run(apps.List(), DateOnly.FromDateTime(DateTime.UtcNow));
                occurrences.PruneOlderThan(DateTimeOffset.UtcNow.AddDays(-7));
                if (result.PartitionsDeleted > 0 || result.DiskGuardTripped)
                {
                    logger.LogInformation("Retention: {Deleted} partitions deleted, disk guard {Guard}",
                        result.PartitionsDeleted, result.DiskGuardTripped ? "TRIPPED" : "ok");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Graceful shutdown (SPEC §13): drain the ingest channels within a 10 s budget, then
/// WAL-checkpoint and close all partitions.
/// </summary>
public sealed class IngestDrainService(IngestPipeline pipeline, PartitionManager partitions,
    ILogger<IngestDrainService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Draining ingest channels…");
        await pipeline.CompleteAndDrainAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        partitions.CloseAll();
    }
}
