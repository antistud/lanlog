using System.Runtime.Versioning;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Polls the Windows Event Log for the app's lifetime (SPEC §6.4). The event log has no push
/// API across machines, so the poll interval is the collection latency.
/// </summary>
/// <remarks>
/// The loop runs whether or not collection is switched on, because the switch lives in the
/// database and can be flipped in the admin UI at any moment. When it is off the loop simply
/// idles; a save wakes it immediately rather than leaving the change to take effect up to an
/// hour later, at the end of the interval that was current when it went to sleep.
/// </remarks>
public sealed class WindowsEventCollectorService(
    WindowsEventCollector collector, WindowsEventSettings settings,
    ILogger<WindowsEventCollectorService> logger)
    : BackgroundService
{
    /// <summary>Signalled on every settings save. Capacity 1 — several saves need only one wake.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.Changed += Wake;
        try
        {
            var announced = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                var options = settings.Current;
                var interval = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds,
                    WindowsEventValidation.MinPollIntervalSeconds, WindowsEventValidation.MaxPollIntervalSeconds));

                if (options.Enabled && options.Sources.Count > 0)
                {
                    if (!announced)
                    {
                        logger.LogInformation(
                            "Windows event collection started: {Sources} source(s), polling every {Interval}",
                            options.Sources.Count, interval);
                        announced = true;
                    }
                    try
                    {
                        // The event log APIs are synchronous and this runs at most once a minute,
                        // so a pooled thread is the right place for it; the token stops it between
                        // channels.
                        await Task.Run(() => collector.CollectOnce(stoppingToken), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // CollectOnce already handles per-target failures; anything reaching here
                        // is unexpected and must still not kill the loop.
                        logger.LogError(ex, "Windows event collection cycle failed");
                    }
                }
                else if (announced)
                {
                    logger.LogInformation("Windows event collection stopped: switched off or no machines configured");
                    announced = false;
                }

                try
                {
                    // Returns early when the settings change, so a shortened interval - or
                    // switching collection on - is honoured now rather than one sleep later.
                    await _wake.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            settings.Changed -= Wake;
        }
    }

    private void Wake()
    {
        // Never blocks: with capacity 1 an already-pending wake is exactly as good as two.
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Two saves raced onto the same free slot; one wake serves both.
            }
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
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
        // Read through the settings object rather than injected as a value: machines and knobs
        // are edited in the admin UI, so every pass has to see the current snapshot.
        services.AddSingleton<Func<WindowsEventOptions>>(sp =>
            () => sp.GetRequiredService<WindowsEventSettings>().Current);
        services.AddSingleton<WindowsEventCollector>();
        services.AddHostedService<WindowsEventCollectorService>();
    }
}

/// <summary>
/// Stands in for the collector on a non-Windows host. Collection can be switched on from the
/// admin UI at any time, and a feature that silently does nothing is the worst outcome, so say
/// so out loud — at startup if it is already on, and on the settings page either way.
/// </summary>
public sealed class WindowsEventUnavailableService(
    WindowsEventSettings settings, ILogger<WindowsEventUnavailableService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Current.Enabled)
        {
            logger.LogWarning("Windows event collection is switched on but this host is not Windows; " +
                              "the event log cannot be read and nothing will be collected.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
