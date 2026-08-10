using System.Text.RegularExpressions;
using Logrr.Server.Ingest;
using Logrr.Storage.Control;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// The agentless Windows Event Log collector (SPEC §6.4): for each configured machine and
/// channel, read everything past the stored high-water mark, map it, and hand it to the same
/// ingest path the HTTP endpoints use.
/// </summary>
/// <remarks>
/// Platform-agnostic by construction — it talks to <see cref="IWindowsEventSource"/>, so the
/// cursor, catch-up and recovery behaviour is testable without a Windows event log.
/// </remarks>
public sealed partial class WindowsEventCollector(
    IWindowsEventSource source,
    WinlogCursorStore cursors,
    AppStore apps,
    IngestService ingest,
    WindowsEventOptions options,
    Func<DateTimeOffset> clock,
    ILogger<WindowsEventCollector> logger)
{
    /// <summary>Consecutive failures per "machine|channel", so an unreachable box logs once, not hourly.</summary>
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App ids already rejected as malformed; logged once each rather than every poll.</summary>
    private readonly HashSet<string> _badAppIds = new(StringComparer.Ordinal);

    /// <summary>One pass over every configured source. Never throws — a bad target is logged and skipped.</summary>
    public void CollectOnce(CancellationToken ct)
    {
        foreach (var src in options.Sources)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var app = ResolveApp(src);
            if (app is null)
            {
                continue;
            }

            foreach (var channel in src.Channels)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                var key = $"{src.Machine}|{channel}";
                try
                {
                    CollectChannel(src, channel, app);
                    if (_failures.Remove(key))
                    {
                        logger.LogInformation("Windows event collection recovered for {Machine}/{Channel}",
                            src.Machine, channel);
                    }
                }
                catch (Exception ex)
                {
                    // First failure is news; the hundredth is noise from a box that is simply
                    // switched off. Drop to Debug until it recovers.
                    var count = _failures.TryGetValue(key, out var n) ? n + 1 : 1;
                    _failures[key] = count;
                    if (count == 1)
                    {
                        logger.LogWarning(ex, "Windows event collection failed for {Machine}/{Channel}",
                            src.Machine, channel);
                    }
                    else
                    {
                        logger.LogDebug(ex, "Windows event collection still failing for {Machine}/{Channel} ({Count} polls)",
                            src.Machine, channel, count);
                    }
                }
            }
        }
    }

    private void CollectChannel(WindowsEventSourceOptions src, string channel, AppRecord app)
    {
        var cursor = cursors.Get(src.Machine, channel) ?? Seed(src, channel, app);
        var maxLevel = WindowsEventMapper.MaxWindowsLevelFor(app.MinimumLevel);

        for (var batch = 0; batch < Math.Max(1, options.MaxBatchesPerPoll); batch++)
        {
            var records = source.Read(new WindowsEventQuery
            {
                Machine = src.Machine,
                Channel = channel,
                AfterRecordId = cursor,
                MaxWindowsLevel = maxLevel,
                MaxEvents = Math.Max(1, options.MaxEventsPerPoll),
            });

            if (records.Count == 0)
            {
                DetectLogCleared(src, channel, cursor);
                return;
            }

            Ship(records, src, channel, app);

            // Record ids are ascending, but Max() rather than Last() so a source that reorders
            // can never walk the cursor backwards and re-ship what it already sent.
            cursor = Math.Max(cursor, records.Max(r => r.RecordId));
            cursors.Set(src.Machine, channel, cursor, clock());

            if (records.Count < options.MaxEventsPerPoll)
            {
                return;
            }
        }

        logger.LogInformation(
            "Windows event collection for {Machine}/{Channel} hit the {Batches}-batch ceiling; " +
            "still behind, continuing next poll", src.Machine, channel, options.MaxBatchesPerPoll);
    }

    private void Ship(IReadOnlyList<WindowsEventRecord> records, WindowsEventSourceOptions src,
        string channel, AppRecord app)
    {
        var events = records.Select(r => WindowsEventMapper.ToLogEvent(r, src.Machine)).ToList();
        var result = ingest.IngestEvents(events, app);

        if (result.Rejected > 0)
        {
            logger.LogWarning(
                "Windows event collection: {Rejected} of {Total} records from {Machine}/{Channel} rejected ({Reason})",
                result.Rejected, records.Count, src.Machine, channel,
                result.Errors.FirstOrDefault() ?? "unknown");
        }
        else
        {
            logger.LogDebug("Collected {Count} events from {Machine}/{Channel} into {App}",
                records.Count, src.Machine, channel, app.Id);
        }
    }

    /// <summary>
    /// First sight of a channel. Default is to start at the tail: an event log holds months of
    /// history, and silently importing all of it on first start would bury the app and mostly be
    /// discarded by the 30-day skew bound anyway (SPEC §6.3).
    /// </summary>
    private long Seed(WindowsEventSourceOptions src, string channel, AppRecord app)
    {
        var hours = Math.Clamp(options.InitialBackfillHours, 0, WindowsEventOptions.MaxInitialBackfillHours);
        if (hours == 0)
        {
            var newest = source.NewestRecordId(src.Machine, channel) ?? 0;
            cursors.Set(src.Machine, channel, newest, clock());
            logger.LogInformation(
                "Windows event collection starting at the tail of {Machine}/{Channel} (record {Record})",
                src.Machine, channel, newest);
            return newest;
        }

        var records = source.Read(new WindowsEventQuery
        {
            Machine = src.Machine,
            Channel = channel,
            Since = clock().AddHours(-hours),
            MaxWindowsLevel = WindowsEventMapper.MaxWindowsLevelFor(app.MinimumLevel),
            MaxEvents = Math.Max(1, options.MaxEventsPerPoll),
        });

        var cursor = records.Count > 0
            ? records.Max(r => r.RecordId)
            : source.NewestRecordId(src.Machine, channel) ?? 0;

        if (records.Count > 0)
        {
            Ship(records, src, channel, app);
        }

        cursors.Set(src.Machine, channel, cursor, clock());
        logger.LogInformation(
            "Windows event collection backfilled {Count} events from the last {Hours}h of {Machine}/{Channel}",
            records.Count, hours, src.Machine, channel);
        return cursor;
    }

    /// <summary>
    /// An empty read is normally just a quiet channel, but it is also what a cleared log looks
    /// like: <c>EventRecordID</c> restarts at 1, so every new record sits below the cursor and
    /// would be skipped forever. Only checked when a read comes back empty, which keeps it off
    /// the hot path.
    /// </summary>
    private void DetectLogCleared(WindowsEventSourceOptions src, string channel, long cursor)
    {
        if (cursor == 0)
        {
            return;
        }
        if (source.NewestRecordId(src.Machine, channel) is not { } newest || newest >= cursor)
        {
            return;
        }

        cursors.Set(src.Machine, channel, 0, clock());
        logger.LogWarning(
            "{Machine}/{Channel} appears to have been cleared (newest record {Newest} is below cursor {Cursor}); " +
            "restarting collection from the beginning of the log", src.Machine, channel, newest, cursor);
    }

    /// <summary>
    /// Look up the target app, creating it on first use so a fresh install collects without a
    /// manual setup step. No token is involved — this path never leaves the process.
    /// </summary>
    private AppRecord? ResolveApp(WindowsEventSourceOptions src)
    {
        var appId = src.AppId?.Trim() ?? "";
        if (!AppIdPattern().IsMatch(appId))
        {
            if (_badAppIds.Add(appId))
            {
                logger.LogError(
                    "Windows event source for {Machine} has AppId '{AppId}', which must match [a-z0-9-]{{3,32}}; skipping",
                    src.Machine, appId);
            }
            return null;
        }

        var existing = apps.Get(appId);
        if (existing is not null)
        {
            return existing.IsEnabled ? existing : null;
        }

        var app = new AppRecord
        {
            Id = appId,
            Name = string.IsNullOrWhiteSpace(src.AppName) ? appId : src.AppName!,
            Description = $"Windows Event Log, collected from {src.Machine}",
            // Warning by default: Application and System are chatty at Information, and the
            // floor also narrows the collector's own query (see MaxWindowsLevelFor), so the
            // noise is never read off the wire in the first place. Editable in the UI after.
            MinimumLevel = src.MinimumLevel ?? LogLevel.Warning,
            IndexedProperties = ["EventId", "ProviderName", "Channel"],
            IsEnabled = true,
            CreatedUtc = clock(),
        };
        apps.Create(app);
        logger.LogInformation("Created app '{AppId}' for Windows event collection from {Machine}",
            appId, src.Machine);
        return app;
    }

    [GeneratedRegex("^[a-z0-9-]{3,32}$")]
    private static partial Regex AppIdPattern();
}
