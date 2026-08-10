using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Reads the Windows Event Log through <c>System.Diagnostics.Eventing.Reader</c> — locally, or
/// over RPC for a remote machine (SPEC §6.4). This is the only Windows-only type in the
/// subsystem; everything else works against <see cref="IWindowsEventSource"/>.
/// </summary>
/// <remarks>
/// Sessions are cached per machine because opening one is an RPC round trip, and dropped on any
/// failure so a rebooted or briefly unreachable box reconnects on the next poll instead of
/// wedging behind a dead handle.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogSource : IWindowsEventSource, IDisposable
{
    private readonly ConcurrentDictionary<string, EventLogSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WindowsEventRecord> Read(WindowsEventQuery query)
    {
        var records = new List<WindowsEventRecord>(Math.Min(query.MaxEvents, 512));
        Execute(query.Machine, session =>
        {
            var logQuery = new EventLogQuery(query.Channel, PathType.LogName, BuildXPath(query))
            {
                Session = session,
            };
            using var reader = new EventLogReader(logQuery);
            while (records.Count < query.MaxEvents)
            {
                using var record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }
                var converted = Convert(record);
                // A record with no id cannot advance the cursor, so shipping it would mean
                // shipping it again on every poll forever. Rare enough to simply skip.
                if (converted is not null)
                {
                    records.Add(converted);
                }
            }
        });
        return records;
    }

    public long? NewestRecordId(string machine, string channel)
    {
        long? newest = null;
        Execute(machine, session =>
        {
            var logQuery = new EventLogQuery(channel, PathType.LogName, "*")
            {
                Session = session,
                ReverseDirection = true, // newest first, so the first record read is the answer
            };
            using var reader = new EventLogReader(logQuery);
            using var record = reader.ReadEvent();
            newest = record?.RecordId;
        });
        return newest;
    }

    /// <summary>
    /// Run an operation against the machine's session, discarding the cached session if it
    /// fails so the next poll starts clean.
    /// </summary>
    private void Execute(string machine, Action<EventLogSession> operation)
    {
        var key = WindowsEventMapper.IsLocal(machine) ? "." : machine.Trim();
        var session = _sessions.GetOrAdd(key, k =>
            k == "." ? EventLogSession.GlobalSession : new EventLogSession(k));
        try
        {
            operation(session);
        }
        catch
        {
            if (_sessions.TryRemove(key, out var stale) && !ReferenceEquals(stale, EventLogSession.GlobalSession))
            {
                stale.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// The event log's own XPath subset. Passed as the query expression rather than embedded in
    /// XML, so <c>&gt;</c> needs no escaping; the only interpolated values are a number and a
    /// formatted timestamp.
    /// </summary>
    internal static string BuildXPath(WindowsEventQuery query)
    {
        var clauses = new List<string>(2);
        if (query.AfterRecordId is { } after)
        {
            clauses.Add($"EventRecordID > {after}");
        }
        if (query.Since is { } since)
        {
            var stamp = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            clauses.Add($"TimeCreated[@SystemTime>='{stamp}']");
        }
        if (query.MaxWindowsLevel is { } max)
        {
            // LogAlways (0) is a routing directive, not a severity, and must survive any ceiling.
            clauses.Add($"(Level=0 or Level<={max})");
        }
        return clauses.Count == 0 ? "*" : $"*[System[{string.Join(" and ", clauses)}]]";
    }

    private static WindowsEventRecord? Convert(EventRecord record)
    {
        if (record.RecordId is not { } recordId)
        {
            return null;
        }

        return new WindowsEventRecord
        {
            RecordId = recordId,
            TimeCreated = record.TimeCreated is { } t
                ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero)
                : DateTimeOffset.UtcNow,
            Channel = record.LogName ?? "",
            ProviderName = record.ProviderName ?? "",
            EventId = record.Id,
            Level = record.Level,
            // Every display name and the rendered description resolve through the publisher's
            // message resources, which are frequently absent when reading a remote machine whose
            // software is not installed here. Each throws EventLogNotFoundException on its own,
            // so each is guarded separately rather than losing the whole record to one gap.
            LevelDisplayName = Try(() => record.LevelDisplayName),
            Description = Try(record.FormatDescription),
            MachineName = record.MachineName,
            UserId = Try(() => record.UserId?.Value),
            TaskDisplayName = Try(() => record.TaskDisplayName),
            OpcodeDisplayName = Try(() => record.OpcodeDisplayName),
            Keywords = Try(() => record.KeywordsDisplayNames is { } k ? string.Join(", ", k) : null),
            ActivityId = record.ActivityId,
            Data = Try(() => record.Properties
                .Select(p => p.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList()) ?? [],
        };
    }

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (EventLogException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            // Thrown for malformed publisher metadata, which is a property-level problem only.
            return default;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            if (!ReferenceEquals(session, EventLogSession.GlobalSession))
            {
                session.Dispose();
            }
        }
        _sessions.Clear();
    }
}
