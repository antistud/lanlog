using Logrr.Contracts;
using Logrr.Storage.Sql;

namespace Logrr.Storage;

/// <summary>
/// Assembles a whole trace: every event carrying one trace id, oldest first, across every app
/// the caller can read (SPEC §7). Deliberately not scoped to a single app — a trace crosses
/// service boundaries, and the screen this feeds exists to put both halves of one request
/// next to each other.
/// </summary>
/// <remarks>
/// Bounded by construction. Each partition is hit through <c>ix_events_trace</c>, which is a
/// seek returning nothing for the (overwhelmingly common) partitions the trace never touched,
/// and only partitions inside the day window are opened at all: ±1 day around the event the
/// user clicked, since a trace does not outlive a request. With no anchor there is no window
/// to compute, so the bound becomes the newest <see cref="UnanchoredPartitions"/> partitions
/// per app — I/O is what needs capping, and that caps it whether the app logs hourly or
/// twice a month.
/// </remarks>
public sealed class TraceReader(PartitionManager partitions)
{
    public const int DefaultLimit = 500;

    private const int MaxLimit = 2000;

    /// <summary>Days either side of the anchor event that are worth opening.</summary>
    private const int AnchorWindowDays = 1;

    /// <summary>How many partitions per app a trace id alone is chased through.</summary>
    private const int UnanchoredPartitions = 7;

    private SqlDialect Dialect => partitions.Dialect;

    /// <summary>
    /// Read the trace. <paramref name="appIds"/> is the set the caller may read — a UI session
    /// passes every app, a scoped token passes its own. <paramref name="near"/> is the
    /// timestamp of the event the trace was opened from, and narrows the scan to the days
    /// around it.
    /// </summary>
    public TraceResponse Read(
        string traceId,
        IEnumerable<string> appIds,
        DateTimeOffset? near = null,
        int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return Empty(traceId);
        }

        DateOnly? fromDay = null, toDay = null;
        if (near is { } anchor)
        {
            var day = StoragePaths.DayOf(anchor);
            fromDay = day.AddDays(-AnchorWindowDays);
            toDay = day.AddDays(AnchorWindowDays);
        }

        // One past the limit is fetched so a full page can be told apart from an exact fit.
        var budget = limit + 1;
        var entries = new List<TraceEntryDto>(Math.Min(budget, 64));
        var partitionsScanned = 0;

        foreach (var appId in appIds)
        {
            var opened = 0;
            foreach (var day in partitions.ExistingDaysDescending(appId))
            {
                if (budget <= 0)
                {
                    break;
                }
                if (toDay is { } max && day > max)
                {
                    continue;
                }
                if (fromDay is { } min && day < min)
                {
                    break; // descending, so everything left is older than the window
                }
                if (fromDay is null && opened >= UnanchoredPartitions)
                {
                    break;
                }

                using var conn = partitions.OpenReader(appId, day);
                if (conn is null)
                {
                    continue;
                }
                opened++;
                partitionsScanned++;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT {EventRow.Columns}
                    FROM {partitions.Table(appId, day)}
                    WHERE trace_id = {cmd.AddParam(traceId)}
                    ORDER BY ts, id
                    {Dialect.LimitClause(budget)};
                    """;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var rowid = Convert.ToInt64(reader.GetValue(0));
                    entries.Add(new TraceEntryDto
                    {
                        AppId = appId,
                        Event = EventRow.Map(reader, day, rowid),
                    });
                    budget--;
                }
            }
            if (budget <= 0)
            {
                break;
            }
        }

        // Partitions are read newest-day-first and app by app; a trace reads chronologically.
        entries.Sort(static (a, b) =>
        {
            var byTime = a.Event.Timestamp.CompareTo(b.Event.Timestamp);
            if (byTime != 0)
            {
                return byTime;
            }
            var byApp = string.CompareOrdinal(a.AppId, b.AppId);
            return byApp != 0 ? byApp : string.CompareOrdinal(a.Event.Id, b.Event.Id);
        });

        var truncated = entries.Count > limit;
        if (truncated)
        {
            entries.RemoveRange(limit, entries.Count - limit);
        }

        return new TraceResponse
        {
            TraceId = traceId,
            Events = entries,
            Truncated = truncated,
            PartitionsScanned = partitionsScanned,
        };
    }

    private static TraceResponse Empty(string traceId) => new()
    {
        TraceId = traceId,
        Events = [],
    };
}
