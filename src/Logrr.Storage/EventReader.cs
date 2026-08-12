using System.Data.Common;
using System.Text;
using Logrr.Contracts;
using Logrr.Core.Filters;
using Logrr.Storage.Sql;

namespace Logrr.Storage;

/// <summary>Parameters for a historical query (SPEC §7).</summary>
public sealed record EventQuery
{
    public required string AppId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public LogLevel? MinLevel { get; init; }
    public string? Text { get; init; }              // full-text search
    public string? Filter { get; init; }            // filter expression (SPEC §7.1)
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>One time bucket of the volume histogram: per-level counts over <see cref="Start"/>..Start+width.</summary>
public sealed record HistogramBucket(DateTimeOffset Start, IReadOnlyDictionary<LogLevel, long> Counts)
{
    public long Total => Counts.Values.Sum();
}

/// <summary>A volume histogram over the query's time range (SPEC §7 explore).</summary>
public sealed record HistogramResult(
    DateTimeOffset From, DateTimeOffset To, TimeSpan BucketSize,
    IReadOnlyList<HistogramBucket> Buckets)
{
    public long Total => Buckets.Sum(b => b.Total);
    public long Max => Buckets.Count == 0 ? 0 : Buckets.Max(b => b.Total);
}

/// <summary>A single facet value and how many matching events carry it.</summary>
public sealed record Facet(string Value, long Count);

/// <summary>Facet breakdown of the current query: counts by level and top sources.</summary>
public sealed record FacetResult(
    IReadOnlyList<(LogLevel Level, long Count)> Levels,
    IReadOnlyList<Facet> Sources);

/// <summary>
/// Reads events across an app's partitions, newest-first, stopping once the page is filled
/// (SPEC §4.1, §7). Opens partitions read-only.
/// </summary>
public sealed class EventReader(PartitionManager partitions)
{
    private const int MaxLimit = 1000;
    private const int MaxAggregatePartitions = 90;

    private SqlDialect Dialect => partitions.Dialect;

    public EventQueryResponse Query(EventQuery query)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        FilterExpression? filter = null;
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            filter = FilterExpression.Parse(query.Filter); // throws FilterParseException on bad input
        }

        var hasCursor = CursorCodec.TryDecode(query.Cursor, out var cursorDay, out var cursorRowid);

        var fromDay = query.From is { } f ? StoragePaths.DayOf(f) : DateOnly.MinValue;
        var toDay = query.To is { } t ? StoragePaths.DayOf(t) : DateOnly.MaxValue;

        // One row past the page size is fetched as a look-ahead: if it exists, there is a
        // next page and the cursor is anchored on the last row we actually return.
        var events = new List<LogEventDto>(limit + 1);
        var partitionsScanned = 0;

        foreach (var day in partitions.ExistingDaysDescending(query.AppId))
        {
            if (day < fromDay || day > toDay)
            {
                continue;
            }
            if (hasCursor && day > cursorDay)
            {
                continue; // already returned in a previous page
            }

            long? idUpperBound = hasCursor && day == cursorDay ? cursorRowid : null;
            var need = limit + 1 - events.Count;
            if (need <= 0)
            {
                break;
            }

            using var conn = partitions.OpenReader(query.AppId, day);
            if (conn is null)
            {
                continue;
            }
            partitionsScanned++;

            foreach (var (_, dto) in ReadPartition(conn, query.AppId, day, query, filter, idUpperBound, need))
            {
                events.Add(dto);
            }

            if (events.Count > limit)
            {
                break;
            }
        }

        string? nextCursor = null;
        if (events.Count > limit)
        {
            events.RemoveAt(events.Count - 1); // drop the look-ahead row
            var last = events[^1];             // anchor the cursor on the last returned row
            if (EventId.TryParse(last.Id, out var d, out var rid))
            {
                nextCursor = CursorCodec.Encode(d, rid);
            }
        }

        return new EventQueryResponse
        {
            Events = events,
            NextCursor = nextCursor,
            PartitionsScanned = partitionsScanned,
        };
    }

    /// <summary>
    /// Append the shared WHERE predicate (time, level, full-text, filter expression) to a command,
    /// so the results table, histogram, and facets all filter identically.
    /// </summary>
    private string BuildWhere(
        DbCommand cmd, string table, EventQuery query, FilterExpression? filter, long? idUpperBound)
    {
        var where = new StringBuilder("1=1");

        if (query.From is { } from)
        {
            where.Append(" AND ts >= ").Append(cmd.AddParam(EventPartition.UnixMicros(from)));
        }
        if (query.To is { } to)
        {
            where.Append(" AND ts <= ").Append(cmd.AddParam(EventPartition.UnixMicros(to)));
        }
        if (query.MinLevel is { } lvl)
        {
            where.Append(" AND level >= ").Append(cmd.AddParam((int)lvl));
        }
        if (idUpperBound is { } bound)
        {
            where.Append(" AND id < ").Append(cmd.AddParam(bound));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Append(" AND ").Append(Dialect.TextSearchPredicate(cmd, table, query.Text));
        }
        if (filter is not null)
        {
            var (sql, ps) = filter.ToSql(Dialect.FilterSql);
            // Re-map @pN parameter names so they don't collide with this command's params.
            var offset = cmd.Parameters.Count;
            for (var i = ps.Count - 1; i >= 0; i--)
            {
                sql = sql.Replace("@p" + i, "@f" + (offset + i));
                var p = cmd.CreateParameter();
                p.ParameterName = "@f" + (offset + i);
                p.Value = ps[i] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            where.Append(" AND (").Append(sql).Append(')');
        }

        return where.ToString();
    }

    private IEnumerable<(long Rowid, LogEventDto Dto)> ReadPartition(
        DbConnection conn, string appId, DateOnly day, EventQuery query, FilterExpression? filter,
        long? idUpperBound, int limit)
    {
        var table = partitions.Table(appId, day);
        using var cmd = conn.CreateCommand();
        var where = BuildWhere(cmd, table, query, filter, idUpperBound);

        cmd.CommandText = $"""
            SELECT {EventRow.Columns}
            FROM {table}
            WHERE {where}
            ORDER BY id DESC
            {Dialect.LimitClause(limit)};
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowid = Convert.ToInt64(reader.GetValue(0));
            yield return (rowid, EventRow.Map(reader, day, rowid));
        }
    }

    /// <summary>
    /// Count how many events of a given type an app has (uses the type index; scans the
    /// most recent partitions, bounded). Powers the occurrence count on event detail.
    /// </summary>
    public long CountByEventType(string appId, long eventType, int maxPartitions = 60)
    {
        long total = 0;
        var scanned = 0;
        foreach (var day in partitions.ExistingDaysDescending(appId))
        {
            if (scanned++ >= maxPartitions)
            {
                break;
            }
            using var conn = partitions.OpenReader(appId, day);
            if (conn is null)
            {
                continue;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {partitions.Table(appId, day)} WHERE event_type = @t;";
            cmd.Add("@t", eventType);
            total += Convert.ToInt64(cmd.ExecuteScalar());
        }
        return total;
    }

    /// <summary>
    /// Bucketed per-level event counts over the query's time range, for the volume histogram.
    /// Uses the same filters as <see cref="Query"/> so the chart matches the results.
    /// </summary>
    public HistogramResult Histogram(EventQuery query, int buckets = 60)
    {
        buckets = Math.Clamp(buckets, 1, 500);
        FilterExpression? filter = string.IsNullOrWhiteSpace(query.Filter)
            ? null : FilterExpression.Parse(query.Filter);

        var to = query.To ?? DateTimeOffset.UtcNow;
        var from = query.From ?? EarliestEvent(query.AppId) ?? to.AddHours(-24);
        if (from >= to)
        {
            from = to.AddMinutes(-1);
        }

        var fromMicros = EventPartition.UnixMicros(from);
        var toMicros = EventPartition.UnixMicros(to);
        var bucketMicros = Math.Max(1, (toMicros - fromMicros) / buckets);

        var tallies = new Dictionary<LogLevel, long>[buckets];
        for (var i = 0; i < buckets; i++)
        {
            tallies[i] = new Dictionary<LogLevel, long>();
        }

        var bounded = query with { From = from, To = to };
        var fromDay = StoragePaths.DayOf(from);
        var toDay = StoragePaths.DayOf(to);
        var scanned = 0;

        foreach (var day in partitions.ExistingDaysDescending(query.AppId))
        {
            if (day > toDay) { continue; }
            if (day < fromDay || scanned >= MaxAggregatePartitions) { break; }
            using var conn = partitions.OpenReader(query.AppId, day);
            if (conn is null)
            {
                continue;
            }
            scanned++;

            var table = partitions.Table(query.AppId, day);
            using var cmd = conn.CreateCommand();
            var where = BuildWhere(cmd, table, bounded, filter, null);
            var pFrom = cmd.AddParam(fromMicros);
            var pBucket = cmd.AddParam(bucketMicros);
            var bucketExpr = Dialect.CastToLong($"(ts - {pFrom}) / {pBucket}");
            cmd.CommandText =
                $"SELECT {bucketExpr} AS b, level, COUNT(*) " +
                $"FROM {table} WHERE {where} GROUP BY {bucketExpr}, level;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var b = (int)Math.Clamp(Convert.ToInt64(reader.GetValue(0)), 0, buckets - 1);
                var level = (LogLevel)Convert.ToInt32(reader.GetValue(1));
                var count = Convert.ToInt64(reader.GetValue(2));
                tallies[b][level] = tallies[b].GetValueOrDefault(level) + count;
            }
        }

        var bucketSize = TimeSpan.FromTicks((toMicros - fromMicros) * 10 / buckets);
        var list = new List<HistogramBucket>(buckets);
        for (var i = 0; i < buckets; i++)
        {
            list.Add(new HistogramBucket(from + TimeSpan.FromTicks(bucketSize.Ticks * i), tallies[i]));
        }
        return new HistogramResult(from, to, bucketSize, list);
    }

    /// <summary>Counts by level and the top sources for the current query (facet sidebar).</summary>
    public FacetResult Facets(EventQuery query, int topSources = 8)
    {
        FilterExpression? filter = string.IsNullOrWhiteSpace(query.Filter)
            ? null : FilterExpression.Parse(query.Filter);

        var levels = new Dictionary<LogLevel, long>();
        var sources = new Dictionary<string, long>();
        var fromDay = query.From is { } f ? StoragePaths.DayOf(f) : DateOnly.MinValue;
        var toDay = query.To is { } t ? StoragePaths.DayOf(t) : DateOnly.MaxValue;
        var scanned = 0;

        foreach (var day in partitions.ExistingDaysDescending(query.AppId))
        {
            if (day > toDay) { continue; }
            if (day < fromDay || scanned >= MaxAggregatePartitions) { break; }
            using var conn = partitions.OpenReader(query.AppId, day);
            if (conn is null)
            {
                continue;
            }
            scanned++;

            var table = partitions.Table(query.AppId, day);
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, table, query, filter, null);
                cmd.CommandText = $"SELECT level, COUNT(*) FROM {table} WHERE {where} GROUP BY level;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var level = (LogLevel)Convert.ToInt32(r.GetValue(0));
                    levels[level] = levels.GetValueOrDefault(level) + Convert.ToInt64(r.GetValue(1));
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                var where = BuildWhere(cmd, table, query, filter, null);
                cmd.CommandText =
                    $"SELECT source, COUNT(*) FROM {table} WHERE {where} AND source IS NOT NULL GROUP BY source;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var src = r.GetString(0);
                    sources[src] = sources.GetValueOrDefault(src) + Convert.ToInt64(r.GetValue(1));
                }
            }
        }

        var levelList = levels.OrderByDescending(kv => (int)kv.Key)
            .Select(kv => (kv.Key, kv.Value)).ToList();
        var sourceList = sources.OrderByDescending(kv => kv.Value).Take(topSources)
            .Select(kv => new Facet(kv.Key, kv.Value)).ToList();
        return new FacetResult(levelList, sourceList);
    }

    private DateTimeOffset? EarliestEvent(string appId)
    {
        DateOnly? earliest = null;
        foreach (var day in partitions.ExistingDaysDescending(appId))
        {
            earliest = day; // descending, so the last seen is the oldest
        }
        return earliest is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
    }

    /// <summary>Fetch a single event by its composite id (SPEC §7 event detail).</summary>
    public LogEventDto? GetById(string appId, string eventId)
    {
        if (!EventId.TryParse(eventId, out var day, out var rowid))
        {
            return null;
        }
        using var conn = partitions.OpenReader(appId, day);
        if (conn is null)
        {
            return null;
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {EventRow.Columns}
            FROM {partitions.Table(appId, day)} WHERE id = @id;
            """;
        cmd.Add("@id", rowid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? EventRow.Map(reader, day, rowid) : null;
    }
}
