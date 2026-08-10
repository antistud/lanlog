using System.Data.Common;
using Logrr.Core;
using Logrr.Core.Filters;
using Logrr.Storage.Control;
using Microsoft.Data.Sqlite;

namespace Logrr.Storage.Sql;

/// <summary>
/// The default backend (SPEC §2, §4): <c>control.db</c> plus one SQLite file per app per UTC
/// day under the data root. Needs nothing installed and nothing configured beyond a writable
/// folder, which is the whole point of it being the default.
/// </summary>
public sealed class SqliteDialect(StoragePaths paths) : SqlDialect
{
    private readonly string _controlConnectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.ControlDbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    public StoragePaths Paths { get; } = paths;

    public override StorageBackend Backend => StorageBackend.Sqlite;

    public override FilterSqlDialect FilterSql => FilterSqlDialect.Sqlite;

    public override string Describe() => $"SQLite files under {Paths.DataRoot}";

    /// <summary>Connection pragmas from SPEC §4.2.</summary>
    internal static void ApplyPragmas(SqliteConnection conn) => Db.Exec(conn, """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = 5000;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -8000;
        """);

    // ---- Control database -------------------------------------------------------------

    public override void EnsureControlCreated() => Paths.EnsureRootDirectories();

    public override DbConnection OpenControl()
    {
        var conn = new SqliteConnection(_controlConnectionString);
        conn.Open();
        ApplyPragmas(conn);
        return conn;
    }

    public override bool ControlExists() => File.Exists(Paths.ControlDbPath);

    public override IReadOnlyList<string> ControlMigrations => ControlSchema.Migrations;

    public override long GetSchemaVersion(DbConnection conn) => Db.Scalar(conn, "PRAGMA user_version;");

    public override void SetSchemaVersion(DbConnection conn, long version) =>
        Db.Exec(conn, $"PRAGMA user_version = {version};");

    // ---- Partitions -------------------------------------------------------------------

    /// <summary>Every partition file holds exactly one table, so the name never varies.</summary>
    public override string PartitionTable(string appId, DateOnly day) => "events";

    public override DbConnection OpenPartitionWriter(
        string appId, DateOnly day, IReadOnlyList<string> indexedProperties)
    {
        Paths.EnsureAppDirectory(appId);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.PartitionPath(appId, day),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        ApplyPragmas(conn);
        Db.Exec(conn, PartitionSchema.BuildDdl(indexedProperties));
        return conn;
    }

    public override DbConnection? OpenPartitionReader(string appId, DateOnly day)
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

    public override IReadOnlyList<DateOnly> PartitionDays(string appId) =>
        Paths.ListPartitions(appId).Select(p => p.Day).ToList();

    public override long PartitionSizeBytes(string appId, DateOnly day) =>
        FileLength(Paths.PartitionPath(appId, day));

    public override bool DeletePartition(string appId, DateOnly day)
    {
        var path = Paths.PartitionPath(appId, day);
        var removed = false;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                    removed |= suffix.Length == 0;
                }
            }
            catch (IOException)
            {
                // A locked or already-removed file — retention retries next cycle.
            }
        }
        return removed;
    }

    public override void InsertEvents(
        DbConnection writer, string table, IReadOnlyList<LogEvent> events, long firstId)
    {
        using var tx = writer.BeginTransaction();

        using (var insert = writer.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = $"""
                INSERT INTO {table} (id, ts, level, template, message, exception, event_type,
                                     trace_id, span_id, source, machine, properties)
                VALUES (@id, @ts, @level, @template, @message, @exception, @event_type,
                        @trace_id, @span_id, @source, @machine, @properties);
                """;
            var pId = insert.Add("@id", null);
            var pTs = insert.Add("@ts", null);
            var pLevel = insert.Add("@level", null);
            var pTemplate = insert.Add("@template", null);
            var pMessage = insert.Add("@message", null);
            var pException = insert.Add("@exception", null);
            var pEventType = insert.Add("@event_type", null);
            var pTrace = insert.Add("@trace_id", null);
            var pSpan = insert.Add("@span_id", null);
            var pSource = insert.Add("@source", null);
            var pMachine = insert.Add("@machine", null);
            var pProps = insert.Add("@properties", null);
            insert.Prepare();

            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                pId.Value = firstId + i;
                pTs.Value = EventPartition.UnixMicros(e.Timestamp);
                pLevel.Value = (int)e.Level;
                pTemplate.Value = (object?)e.Template ?? DBNull.Value;
                pMessage.Value = e.Message;
                pException.Value = (object?)e.Exception ?? DBNull.Value;
                pEventType.Value = (object?)e.EventType ?? DBNull.Value;
                pTrace.Value = (object?)e.TraceId ?? DBNull.Value;
                pSpan.Value = (object?)e.SpanId ?? DBNull.Value;
                pSource.Value = (object?)e.Source ?? DBNull.Value;
                pMachine.Value = (object?)e.Machine ?? DBNull.Value;
                pProps.Value = PropertyValue.ToJson(e.Properties);
                insert.ExecuteNonQuery();
            }
        }

        // Maintain FTS explicitly — one insert for the whole batch, not a per-row trigger.
        using (var fts = writer.CreateCommand())
        {
            fts.Transaction = tx;
            fts.CommandText = $"""
                INSERT INTO events_fts (rowid, message, exception)
                SELECT id, message, exception FROM {table} WHERE id >= @firstId;
                """;
            fts.Add("@firstId", firstId);
            fts.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public override void CheckpointAndClose(DbConnection writer)
    {
        try
        {
            Db.Exec(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
            // Best-effort on shutdown.
        }
        writer.Close();
    }

    public override bool SupportsDiskGuard => true;

    public override long FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Paths.DataRoot));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue; // can't tell → don't purge
        }
    }

    // ---- SQL fragments ----------------------------------------------------------------

    public override string LimitClause(int count) => $"LIMIT {count}";

    public override string LimitClause(string parameterName) => $"LIMIT {parameterName}";

    public override string AnyRowsScalar(string table) => $"SELECT EXISTS(SELECT 1 FROM {table});";

    public override string CaseInsensitiveEquals(string column, string parameterName) =>
        $"{column} = {parameterName} COLLATE NOCASE";

    public override string CastToLong(string expression) => $"CAST({expression} AS INTEGER)";

    public override string TextSearchPredicate(DbCommand cmd, string table, string text) =>
        $"id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH {cmd.AddParam(text)})";

    private static long FileLength(string path)
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
