using System.Data.Common;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;

namespace Logrr.Storage;

/// <summary>Computes per-app counters for the stats endpoint and tiles (SPEC §7, §8.6).</summary>
public sealed class StatsReader(PartitionManager partitions, AckStore acks)
{
    /// <summary>Error and Fatal — the levels the overview alerts on.</summary>
    private const int AlertLevel = (int)LogLevel.Error;

    public AppStatsDto GetStats(string appId, DateTimeOffset nowUtc)
    {
        long total = 0;
        var byLevel = new Dictionary<string, long>();
        var byHour = new Dictionary<string, long>();
        long storageBytes = 0;
        long unacknowledgedErrors = 0;
        DateTimeOffset? lastEvent = null;

        var since24h = EventPartition.UnixMicros(nowUtc.AddHours(-24));
        var ackSnapshot = acks.Snapshot(appId);
        var ackedTypesJson = ackSnapshot.IsEmpty ? null : JsonSerializer.Serialize(ackSnapshot.ByEventType);

        foreach (var day in partitions.ExistingDaysDescending(appId))
        {
            storageBytes += partitions.Dialect.PartitionSizeBytes(appId, day);

            using var conn = partitions.OpenReader(appId, day);
            if (conn is null)
            {
                continue;
            }
            var table = partitions.Table(appId, day);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT level, COUNT(*), MAX(ts) FROM {table} GROUP BY level;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var level = (LogLevel)Convert.ToInt32(reader.GetValue(0));
                    var count = Convert.ToInt64(reader.GetValue(1));
                    total += count;
                    byLevel[level.ToString()] = byLevel.GetValueOrDefault(level.ToString()) + count;
                    if (!reader.IsDBNull(2))
                    {
                        var ts = DateTimeOffset.UnixEpoch.AddTicks(Convert.ToInt64(reader.GetValue(2)) * 10);
                        if (lastEvent is null || ts > lastEvent)
                        {
                            lastEvent = ts;
                        }
                    }
                }
            }

            // Per-hour buckets for the last 24h only.
            using (var cmd = conn.CreateCommand())
            {
                // Integer division on both backends, so the hour index is exact either way.
                cmd.CommandText = $"""
                    SELECT ts / 3600000000 AS hour, COUNT(*)
                    FROM {table} WHERE ts >= @since
                    GROUP BY ts / 3600000000;
                    """;
                cmd.Add("@since", since24h);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var hourStart = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(reader.GetValue(0)) * 3600);
                    var key = hourStart.UtcDateTime.ToString("yyyy-MM-ddTHH:00Z");
                    byHour[key] = byHour.GetValueOrDefault(key) + Convert.ToInt64(reader.GetValue(1));
                }
            }

            if (ackedTypesJson is not null)
            {
                unacknowledgedErrors += CountUnacknowledgedErrors(
                    conn, partitions.Dialect, table, ackSnapshot, ackedTypesJson);
            }
        }

        // With no acks the extra per-partition scan is skipped: everything is unacknowledged.
        if (ackSnapshot.IsEmpty)
        {
            unacknowledgedErrors = byLevel.GetValueOrDefault(nameof(LogLevel.Error))
                + byLevel.GetValueOrDefault(nameof(LogLevel.Fatal));
        }

        return new AppStatsDto
        {
            AppId = appId,
            TotalEvents = total,
            CountByLevel = byLevel,
            CountByHour = byHour,
            StorageBytes = storageBytes,
            LastEventUtc = lastEvent,
            UnacknowledgedErrors = unacknowledgedErrors,
        };
    }

    /// <summary>
    /// Errors in this partition that no ack covers: newer than the app-wide watermark, and —
    /// for events carrying an event type — newer than that type's own watermark.
    /// </summary>
    /// <remarks>
    /// The per-type watermarks arrive as a JSON object rather than a temp table so this stays a
    /// single parameterised statement. Shredding it is the one place the two backends need
    /// different SQL: SQLite has <c>json_each</c>, SQL Server has <c>OPENJSON</c>.
    /// </remarks>
    private static long CountUnacknowledgedErrors(
        DbConnection conn, SqlDialect dialect, string table, AckSnapshot acks, string ackedTypesJson)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = dialect.IsSqlServer
            ? $"""
              SELECT COUNT(*) FROM {table} e
              WHERE e.level >= {AlertLevel} AND e.ts > @appWide
                AND NOT EXISTS (
                  SELECT 1 FROM OPENJSON(@types) AS a
                  WHERE CAST(a.[key] AS BIGINT) = e.event_type
                    AND e.ts <= CAST(a.[value] AS BIGINT));
              """
            : $"""
              SELECT COUNT(*) FROM {table}
              WHERE level >= {AlertLevel} AND ts > @appWide
                AND NOT EXISTS (
                  SELECT 1 FROM json_each(@types) AS a
                  WHERE CAST(a.key AS INTEGER) = {table}.event_type
                    AND {table}.ts <= CAST(a.value AS INTEGER));
              """;
        cmd.Add("@appWide", acks.AppWideThroughTs);
        cmd.Add("@types", ackedTypesJson);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Total stored bytes for an app across its partitions (SPEC §4.6 size cap).</summary>
    public long StorageBytes(string appId) =>
        partitions.ExistingDaysDescending(appId).Sum(day => partitions.Dialect.PartitionSizeBytes(appId, day));
}
