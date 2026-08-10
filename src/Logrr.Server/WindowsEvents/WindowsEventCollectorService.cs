using System.Runtime.Versioning;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Polls the Windows Event Log for the app's lifetime (SPEC §6.4). The event log has no push
/// API across machines, so the poll interval is the collection latency.
/// </summary>
public sealed class WindowsEventCollectorService(
    WindowsEventCollector collector, WindowsEventOptions options, ILogger<WindowsEventCollectorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 5, 3600));
        logger.LogInformation("Windows event collection started: {Sources} source(s), polling every {Interval}",
            options.Sources.Count, interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                // The event log APIs are synchronous and this runs at most once a minute, so a
                // pooled thread is the right place for it; the token stops it between channels.
                await Task.Run(() => collector.CollectOnce(stoppingToken), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // CollectOnce already handles per-target failures; anything reaching here is
                // unexpected and must still not kill the loop.
                logger.LogError(ex, "Windows event collection cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Registration for the collector. Windows-only in one place so <c>Program.cs</c> can gate it
/// behind a single <c>OperatingSystem.IsWindows()</c> check that the platform-compatibility
/// analyser understands.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsEventRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton<IWindowsEventSource, WindowsEventLogSource>();
        services.AddSingleton<WindowsEventCollector>();
        services.AddHostedService<WindowsEventCollectorService>();
    }
}

/// <summary>
/// Stands in for the collector when it is configured on a non-Windows host. Enabling a feature
/// and having it silently do nothing is the worst outcome, so say so out loud at startup.
/// </summary>
public sealed class WindowsEventUnavailableService(ILogger<WindowsEventUnavailableService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Logrr:WindowsEvents:Enabled is set but this host is not Windows; " +
                          "event log collection is disabled.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
