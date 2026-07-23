using Logrr.Contracts;
using Microsoft.Data.Sqlite;

namespace Logrr.Storage;

/// <summary>Computes per-app counters for the stats endpoint and tiles (SPEC §7, §8.6).</summary>
public sealed class StatsReader(PartitionManager partitions)
{
    public AppStatsDto GetStats(string appId, DateTimeOffset nowUtc)
    {
        long total = 0;
        var byLevel = new Dictionary<string, long>();
        var byHour = new Dictionary<string, long>();
        long storageBytes = 0;
        DateTimeOffset? lastEvent = null;

        var since24h = EventPartition.UnixMicros(nowUtc.AddHours(-24));

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
        }

        return new AppStatsDto
        {
            AppId = appId,
            TotalEvents = total,
            CountByLevel = byLevel,
            CountByHour = byHour,
            StorageBytes = storageBytes,
            LastEventUtc = lastEvent,
        };
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
