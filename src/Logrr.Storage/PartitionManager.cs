using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Storage;

/// <summary>
/// Opens and caches one writer <see cref="EventPartition"/> per (app, UTC day), and hands
/// out read-only connections for queries (SPEC §4.1, §4.2). Thread-safe.
/// </summary>
public sealed class PartitionManager(SqlDialect dialect) : IDisposable
{
    private readonly Dictionary<(string App, DateOnly Day), EventPartition> _writers = new();
    private readonly Lock _gate = new();

    /// <summary>Convenience for the default file-backed layout (and for tests).</summary>
    public PartitionManager(StoragePaths paths) : this(new SqliteDialect(paths))
    {
    }

    public SqlDialect Dialect { get; } = dialect;

    /// <summary>
    /// Get (opening if necessary) the writer partition for an app-day. The DDL — including
    /// an index per indexed property from the app's config snapshot — is applied at creation.
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

            var partition = new EventPartition(Dialect, appId, day, indexedProperties);
            _writers[key] = partition;
            return partition;
        }
    }

    /// <summary>
    /// Open a read-only connection to an existing partition, or null if there is none.
    /// Callers dispose the connection when done.
    /// </summary>
    public DbConnection? OpenReader(string appId, DateOnly day) =>
        Dialect.OpenPartitionReader(appId, day);

    /// <summary>The table an app-day's events live in, for building queries against it.</summary>
    public string Table(string appId, DateOnly day) => Dialect.PartitionTable(appId, day);

    /// <summary>Partition days that exist for an app, newest first (SPEC §4.1).</summary>
    public IReadOnlyList<DateOnly> ExistingDaysDescending(string appId) =>
        Dialect.PartitionDays(appId).OrderDescending().ToList();

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
