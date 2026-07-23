using Microsoft.Data.Sqlite;

namespace Logrr.Storage;

/// <summary>
/// Opens and caches one writer <see cref="EventPartition"/> per (app, UTC day), and hands
/// out read-only connections for queries (SPEC §4.1, §4.2). Thread-safe.
/// </summary>
public sealed class PartitionManager(StoragePaths paths) : IDisposable
{
    private readonly Dictionary<(string App, DateOnly Day), EventPartition> _writers = new();
    private readonly Lock _gate = new();

    public StoragePaths Paths { get; } = paths;

    /// <summary>
    /// Get (opening if necessary) the writer partition for an app-day. The DDL — including
    /// expression indexes for the app's indexed properties — is applied at creation.
    /// </summary>
    public EventPartition GetWriter(string appId, DateOnly day, IReadOnlyList<string> indexedProperties)
    {
        var key = (appId, day);
        lock (_gate)
        {
            if (_writers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            Paths.EnsureAppDirectory(appId);
            var ddl = PartitionSchema.BuildDdl(indexedProperties);
            var partition = new EventPartition(appId, day, Paths.PartitionPath(appId, day), ddl);
            _writers[key] = partition;
            return partition;
        }
    }

    /// <summary>
    /// Open a read-only connection to an existing partition, or null if the file is absent.
    /// Callers dispose the connection when done.
    /// </summary>
    public SqliteConnection? OpenReader(string appId, DateOnly day)
    {
        var path = Paths.PartitionPath(appId, day);
        if (!File.Exists(path))
        {
            return null;
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Partition days that exist on disk for an app, newest first (SPEC §4.1).</summary>
    public IReadOnlyList<DateOnly> ExistingDaysDescending(string appId) =>
        Paths.ListPartitions(appId).Select(p => p.Day).OrderDescending().ToList();

    /// <summary>Close an open writer for a specific app-day, if any (used before deletion).</summary>
    public void ClosePartition(string appId, DateOnly day)
    {
        lock (_gate)
        {
            if (_writers.Remove((appId, day), out var partition))
            {
                partition.CheckpointAndClose();
                partition.Dispose();
            }
        }
    }

    /// <summary>Checkpoint and close every open writer (graceful shutdown).</summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var p in _writers.Values)
            {
                p.CheckpointAndClose();
                p.Dispose();
            }
            _writers.Clear();
        }
    }

    public void Dispose() => CloseAll();
}
