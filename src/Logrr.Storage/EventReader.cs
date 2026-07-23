using System.Text;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core.Filters;
using Microsoft.Data.Sqlite;

namespace Logrr.Storage;

/// <summary>Parameters for a historical query (SPEC §7).</summary>
public sealed record EventQuery
{
    public required string AppId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public LogLevel? MinLevel { get; init; }
    public string? Text { get; init; }              // full-text (FTS5 MATCH)
    public string? Filter { get; init; }            // filter expression (SPEC §7.1)
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Reads events across an app's partitions, newest-first, stopping once the page is filled
/// (SPEC §4.1, §7). Opens partitions read-only.
/// </summary>
public sealed class EventReader(PartitionManager partitions)
{
    private const int MaxLimit = 1000;

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

            foreach (var (_, dto) in ReadPartition(conn, day, query, filter, idUpperBound, need))
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

    private static IEnumerable<(long Rowid, LogEventDto Dto)> ReadPartition(
        SqliteConnection conn, DateOnly day, EventQuery query, FilterExpression? filter,
        long? idUpperBound, int limit)
    {
        var where = new StringBuilder("1=1");
        using var cmd = conn.CreateCommand();

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
            where.Append(" AND id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH ")
                 .Append(cmd.AddParam(query.Text)).Append(')');
        }
        if (filter is not null)
        {
            var (sql, ps) = filter.ToSql();
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

        cmd.CommandText = $"""
            SELECT id, ts, level, template, message, exception, event_type,
                   trace_id, span_id, source, machine, properties
            FROM events
            WHERE {where}
            ORDER BY id DESC
            LIMIT {limit};
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowid = reader.GetInt64(0);
            yield return (rowid, MapDto(reader, day, rowid));
        }
    }

    private static LogEventDto MapDto(SqliteDataReader r, DateOnly day, long rowid)
    {
        var micros = r.GetInt64(1);
        JsonElement? props = null;
        if (!r.IsDBNull(11))
        {
            var json = r.GetString(11);
            if (!string.IsNullOrEmpty(json))
            {
                props = JsonSerializer.Deserialize<JsonElement>(json);
            }
        }

        return new LogEventDto
        {
            Id = EventId.Format(day, rowid),
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(micros * 10),
            Level = (LogLevel)r.GetInt32(2),
            Template = r.IsDBNull(3) ? null : r.GetString(3),
            Message = r.GetString(4),
            Exception = r.IsDBNull(5) ? null : r.GetString(5),
            EventType = r.IsDBNull(6) ? null : r.GetInt64(6),
            TraceId = r.IsDBNull(7) ? null : r.GetString(7),
            SpanId = r.IsDBNull(8) ? null : r.GetString(8),
            Source = r.IsDBNull(9) ? null : r.GetString(9),
            Machine = r.IsDBNull(10) ? null : r.GetString(10),
            Properties = props,
        };
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
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE event_type = $t;";
            cmd.Add("$t", eventType);
            total += Convert.ToInt64(cmd.ExecuteScalar());
        }
        return total;
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
        cmd.CommandText = """
            SELECT id, ts, level, template, message, exception, event_type,
                   trace_id, span_id, source, machine, properties
            FROM events WHERE id = $id;
            """;
        cmd.Add("$id", rowid);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapDto(reader, day, rowid) : null;
    }
}
