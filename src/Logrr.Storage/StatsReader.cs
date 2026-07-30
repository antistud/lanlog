using System.Text.Json;
using Logrr.Contracts;
using Logrr.Storage.Control;
using Microsoft.Data.Sqlite;

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

        foreach (var (day, path) in partitions.Paths.ListPartitions(appId))
        {
            storageBytes += SafeLength(path);

            using var conn = partitions.OpenReader(appId, day);
            if (conn is null)
            {
                continue;
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT level, COUNT(*), MAX(ts) FROM events GROUP BY level;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var level = (LogLevel)reader.GetInt32(0);
                    var count = reader.GetInt64(1);
                    total += count;
                    byLevel[level.ToString()] = byLevel.GetValueOrDefault(level.ToString()) + count;
                    if (!reader.IsDBNull(2))
                    {
                        var ts = DateTimeOffset.UnixEpoch.AddTicks(reader.GetInt64(2) * 10);
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
                cmd.CommandText = """
                    SELECT ts / 3600000000 AS hour, COUNT(*)
                    FROM events WHERE ts >= $since
                    GROUP BY hour;
                    """;
                cmd.Add("$since", since24h);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var hourStart = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0) * 3600);
                    var key = hourStart.UtcDateTime.ToString("yyyy-MM-ddTHH:00Z");
                    byHour[key] = byHour.GetValueOrDefault(key) + reader.GetInt64(1);
                }
            }

            if (ackedTypesJson is not null)
            {
                unacknowledgedErrors += CountUnacknowledgedErrors(conn, ackSnapshot, ackedTypesJson);
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
    private static long CountUnacknowledgedErrors(SqliteConnection conn, AckSnapshot acks, string ackedTypesJson)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM events
            WHERE level >= {AlertLevel} AND ts > $appWide
              AND NOT EXISTS (
                SELECT 1 FROM json_each($types) AS a
                WHERE CAST(a.key AS INTEGER) = events.event_type
                  AND events.ts <= CAST(a.value AS INTEGER));
            """;
        cmd.Add("$appWide", acks.AppWideThroughTs);
        cmd.Add("$types", ackedTypesJson);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Total on-disk bytes for an app across its partitions (SPEC §4.6 size cap).</summary>
    public long StorageBytes(string appId) =>
        partitions.Paths.ListPartitions(appId).Sum(p => SafeLength(p.Path));

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
