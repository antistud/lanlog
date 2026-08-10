using System.Data.Common;
using Logrr.Core;
using Logrr.Storage.Sql;

namespace Logrr.Storage;

/// <summary>
/// One open partition — an app's UTC day — with its single writer connection (SPEC §4.2).
/// Owned by the <see cref="PartitionManager"/>; not thread-safe, the batch writer serialises
/// access. What a partition physically is depends on the backend: a SQLite file, or a table in
/// SQL Server. Everything above this class only sees "an app-day you insert batches into".
/// </summary>
public sealed class EventPartition : IDisposable
{
    private readonly SqlDialect _dialect;
    private readonly DbConnection _writer;
    private readonly string _table;

    public string AppId { get; }
    public DateOnly Day { get; }

    internal EventPartition(
        SqlDialect dialect, string appId, DateOnly day, IReadOnlyList<string> indexedProperties)
    {
        _dialect = dialect;
        AppId = appId;
        Day = day;
        _writer = dialect.OpenPartitionWriter(appId, day, indexedProperties);
        _table = dialect.PartitionTable(appId, day);
    }

    /// <summary>Convert a timestamp to the stored representation: unix microseconds UTC.</summary>
    public static long UnixMicros(DateTimeOffset ts) =>
        (ts.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    /// <summary>
    /// Insert a batch in a single transaction (SPEC §4.3, §4.5). Returns the assigned ids paired
    /// with their events so the caller can fan out post-commit.
    /// </summary>
    /// <remarks>
    /// Ids are assigned here rather than by the database. They have to be contiguous and
    /// predictable — the composite event id and the paging cursor are both built from them —
    /// and only one writer ever touches a partition, so reading the current maximum and
    /// counting up from it is exact. It also means the two backends number rows identically
    /// instead of relying on SQLite rowids and SQL Server identity columns behaving alike.
    /// </remarks>
    public IReadOnlyList<(long Rowid, LogEvent Event)> InsertBatch(IReadOnlyList<LogEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        long preMax;
        using (var maxCmd = _writer.CreateCommand())
        {
            maxCmd.CommandText = $"SELECT COALESCE(MAX(id), 0) FROM {_table};";
            preMax = Convert.ToInt64(maxCmd.ExecuteScalar());
        }

        var firstId = preMax + 1;
        _dialect.InsertEvents(_writer, _table, events, firstId);

        var result = new List<(long, LogEvent)>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            result.Add((firstId + i, events[i]));
        }
        return result;
    }

    /// <summary>Flush and close the writer (graceful shutdown, SPEC §13).</summary>
    public void CheckpointAndClose() => _dialect.CheckpointAndClose(_writer);

    public void Dispose() => _writer.Dispose();
}
