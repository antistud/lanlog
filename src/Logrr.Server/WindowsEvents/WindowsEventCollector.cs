using System.Collections.Concurrent;
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
    /// <summary>
    /// Per-target health, keyed "machine|channel". Also what the admin status page reads, so it
    /// is a <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>: the
    /// UI reads it on a render thread while a poll writes it on a pooled one.
    /// </summary>
    private readonly ConcurrentDictionary<string, TargetHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App ids already rejected as malformed; logged once each rather than every poll.</summary>
    private readonly HashSet<string> _badAppIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Serialises polls. The timer and the admin page's "Collect now" both call
    /// <see cref="CollectOnce"/>, and two passes at once would read the same cursor twice and
    /// ship every event in between as a duplicate.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class TargetHealth
    {
        public DateTimeOffset? LastSuccessUtc;
        public DateTimeOffset? LastAttemptUtc;
        public int ConsecutiveFailures;
        public string? LastError;
        public long EventsCollected;
    }

    /// <summary>When the last full pass finished, for the status page.</summary>
    public DateTimeOffset? LastPollUtc { get; private set; }

    /// <summary>True while a pass is running, so the UI can disable its button.</summary>
    public bool IsCollecting => _gate.CurrentCount == 0;

    /// <summary>One pass over every configured source. Never throws — a bad target is logged and skipped.</summary>
    public void CollectOnce(CancellationToken ct)
    {
        // A poll already in flight has just done this work; skipping beats queueing a
        // redundant pass behind it.
        if (!_gate.Wait(TimeSpan.Zero, ct))
        {
            return;
        }
        try
        {
            CollectAllTargets(ct);
            LastPollUtc = clock();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void CollectAllTargets(CancellationToken ct)
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

            foreach (var channel in src.EffectiveChannels)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                var health = _health.GetOrAdd(TargetKey(src.Machine, channel), _ => new TargetHealth());
                health.LastAttemptUtc = clock();
                try
                {
                    CollectChannel(src, channel, app, health);
                    health.LastSuccessUtc = clock();
                    health.LastError = null;
                    if (health.ConsecutiveFailures > 0)
                    {
                        health.ConsecutiveFailures = 0;
                        logger.LogInformation("Windows event collection recovered for {Machine}/{Channel}",
                            src.Machine, channel);
                    }
                }
                catch (Exception ex)
                {
                    // First failure is news; the hundredth is noise from a box that is simply
                    // switched off. Drop to Debug until it recovers.
                    health.ConsecutiveFailures++;
                    health.LastError = ex.Message;
                    if (health.ConsecutiveFailures == 1)
                    {
                        logger.LogWarning(ex, "Windows event collection failed for {Machine}/{Channel}",
                            src.Machine, channel);
                    }
                    else
                    {
                        logger.LogDebug(ex, "Windows event collection still failing for {Machine}/{Channel} ({Count} polls)",
                            src.Machine, channel, health.ConsecutiveFailures);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Live status for every configured target, joined onto its stored cursor. Built from the
    /// configuration rather than from what has been collected, so a machine that has never once
    /// been reachable still appears — as a failing row, which is the case you need to see.
    /// </summary>
    public IReadOnlyList<WindowsEventTargetStatus> Status()
    {
        var byKey = cursors.List().ToDictionary(
            c => TargetKey(c.Machine, c.Channel), StringComparer.OrdinalIgnoreCase);

        var rows = new List<WindowsEventTargetStatus>();
        foreach (var src in options.Sources)
        {
            foreach (var channel in src.EffectiveChannels)
            {
                var key = TargetKey(src.Machine, channel);
                byKey.TryGetValue(key, out var cursor);
                _health.TryGetValue(key, out var health);

                rows.Add(new WindowsEventTargetStatus
                {
                    Machine = WindowsEventMapper.NormalizeMachine(src.Machine),
                    ConfiguredMachine = src.Machine,
                    Channel = channel,
                    AppId = src.AppId,
                    LastRecordId = cursor?.LastRecordId,
                    CursorUpdatedUtc = cursor?.UpdatedUtc,
                    LastSuccessUtc = health?.LastSuccessUtc,
                    LastAttemptUtc = health?.LastAttemptUtc,
                    ConsecutiveFailures = health?.ConsecutiveFailures ?? 0,
                    LastError = health?.LastError,
                    EventsCollected = health?.EventsCollected ?? 0,
                });
            }
        }
        return rows;
    }

    private static string TargetKey(string machine, string channel) =>
        $"{machine.Trim()}|{channel.Trim()}";

    private void CollectChannel(WindowsEventSourceOptions src, string channel, AppRecord app, TargetHealth health)
    {
        var cursor = cursors.Get(src.Machine, channel) ?? Seed(src, channel, app, health);
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

            Ship(records, src, channel, app, health);

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
        string channel, AppRecord app, TargetHealth health)
    {
        var events = records.Select(r => WindowsEventMapper.ToLogEvent(r, src.Machine)).ToList();
        var result = ingest.IngestEvents(events, app);
        health.EventsCollected += result.Accepted;

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
    private long Seed(WindowsEventSourceOptions src, string channel, AppRecord app, TargetHealth health)
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
            Ship(records, src, channel, app, health);
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
