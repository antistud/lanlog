using Logrr.Core;
using Microsoft.Data.Sqlite;

namespace Logrr.Storage;

/// <summary>
/// One open partition (an app's UTC-day SQLite file) with its single writer connection
/// (SPEC §4.2). Owned by the <see cref="PartitionManager"/>; not thread-safe — the batch
/// writer serialises access.
/// </summary>
public sealed class EventPartition : IDisposable
{
    private readonly SqliteConnection _writer;

    public string AppId { get; }
    public DateOnly Day { get; }

    internal EventPartition(string appId, DateOnly day, string path, string ddl)
    {
        AppId = appId;
        Day = day;

        _writer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _writer.Open();
        Sqlite.ApplyPragmas(_writer);
        Sqlite.Exec(_writer, ddl);
    }

    /// <summary>Convert a timestamp to the stored representation: unix microseconds UTC.</summary>
    public static long UnixMicros(DateTimeOffset ts) =>
        (ts.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    /// <summary>
    /// Insert a batch in a single transaction with a prepared statement, then maintain the
    /// FTS index with one insert for the batch (SPEC §4.3, §4.5). Returns the assigned
    /// rowids paired with their events so the caller can fan out post-commit.
    /// </summary>
    public IReadOnlyList<(long Rowid, LogEvent Event)> InsertBatch(IReadOnlyList<LogEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        using var tx = _writer.BeginTransaction();

        long preMax;
        using (var maxCmd = _writer.CreateCommand())
        {
            maxCmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM events;";
            preMax = Convert.ToInt64(maxCmd.ExecuteScalar());
        }

        using (var insert = _writer.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO events (ts, level, template, message, exception, event_type,
                                    trace_id, span_id, source, machine, properties)
                VALUES ($ts, $level, $template, $message, $exception, $event_type,
                        $trace_id, $span_id, $source, $machine, $properties);
                """;
            var pTs = insert.Add("$ts", null);
            var pLevel = insert.Add("$level", null);
            var pTemplate = insert.Add("$template", null);
            var pMessage = insert.Add("$message", null);
            var pException = insert.Add("$exception", null);
            var pEventType = insert.Add("$event_type", null);
            var pTrace = insert.Add("$trace_id", null);
            var pSpan = insert.Add("$span_id", null);
            var pSource = insert.Add("$source", null);
            var pMachine = insert.Add("$machine", null);
            var pProps = insert.Add("$properties", null);
            insert.Prepare();

            foreach (var e in events)
            {
                pTs.Value = UnixMicros(e.Timestamp);
                pLevel.Value = (int)e.Level;
                pTemplate.Value = (object?)e.Template ?? DBNull.Value;
                pMessage.Value = e.Message;
                pException.Value = (object?)e.Exception ?? DBNull.Value;
                pEventType.Value = e.EventType;
                pTrace.Value = (object?)e.TraceId ?? DBNull.Value;
                pSpan.Value = (object?)e.SpanId ?? DBNull.Value;
                pSource.Value = (object?)e.Source ?? DBNull.Value;
                pMachine.Value = (object?)e.Machine ?? DBNull.Value;
                pProps.Value = PropertyValue.ToJson(e.Properties);
                insert.ExecuteNonQuery();
            }
        }

        // Maintain FTS explicitly — one insert for the whole batch, not a per-row trigger.
        using (var fts = _writer.CreateCommand())
        {
            fts.CommandText = """
                INSERT INTO events_fts (rowid, message, exception)
                SELECT id, message, exception FROM events WHERE id > $preMax;
                """;
            fts.Add("$preMax", preMax);
            fts.ExecuteNonQuery();
        }

        tx.Commit();

        // Rowids are contiguous from preMax+1 because a single writer serialises inserts.
        var result = new List<(long, LogEvent)>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            result.Add((preMax + 1 + i, events[i]));
        }
        return result;
    }

    /// <summary>Checkpoint the WAL and close the writer (graceful shutdown, SPEC §13).</summary>
    public void CheckpointAndClose()
    {
        try
        {
            Sqlite.Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // Best-effort on shutdown.
        }
        _writer.Close();
    }

    public void Dispose() => _writer.Dispose();
}
