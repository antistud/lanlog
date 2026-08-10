using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Logrr.Core;
using Logrr.Core.Filters;
using Microsoft.Data.SqlClient;

namespace Logrr.Storage.Sql;

/// <summary>
/// The SQL Server backend (SPEC §4.7). The partition model carries over unchanged: an app-day
/// is one table, <c>events_{app}_{yyyyMMdd}</c>, so retention still drops a whole partition as
/// a metadata operation rather than deleting millions of rows, and a query still narrows to a
/// single day's table before it filters.
/// </summary>
public sealed partial class SqlServerDialect : SqlDialect
{
    /// <summary>
    /// SQL Server caps a statement at 2100 parameters. Twelve columns per event means 175 rows
    /// fits comfortably; batches larger than this are sent as several multi-row inserts inside
    /// the one transaction.
    /// </summary>
    private const int MaxRowsPerInsert = 175;

    private readonly string _connectionString;

    public SqlServerDialect(string connectionString, string schema)
    {
        _connectionString = connectionString;
        Schema = string.IsNullOrWhiteSpace(schema) ? "logrr" : schema.Trim();
        if (!SafeIdentifier().IsMatch(Schema))
        {
            throw new ArgumentException(
                $"Logrr:Storage:Schema must be a plain SQL identifier; '{schema}' is not.", nameof(schema));
        }
    }

    public string Schema { get; }

    public override StorageBackend Backend => StorageBackend.SqlServer;

    public override FilterSqlDialect FilterSql => FilterSqlDialect.SqlServer;

    public override string Describe()
    {
        var b = new SqlConnectionStringBuilder(_connectionString);
        return $"SQL Server {b.DataSource}, database {b.InitialCatalog}, schema {Schema}";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    /// <summary>App ids as the admin API mints them (SPEC §5.1): a lowercase slug.</summary>
    [GeneratedRegex("^[a-z0-9-]{1,32}$")]
    private static partial Regex AppIdSlug();

    // ---- Control database -------------------------------------------------------------

    public override void EnsureControlCreated()
    {
        using var conn = OpenRaw();
        Db.Exec(conn, $"IF SCHEMA_ID('{Schema}') IS NULL EXEC('CREATE SCHEMA [{Schema}]');");
        Db.Exec(conn, $"""
            IF OBJECT_ID('{Schema}.schema_version') IS NULL
              CREATE TABLE [{Schema}].[schema_version] (
                lock_row BIT NOT NULL PRIMARY KEY DEFAULT 1 CHECK (lock_row = 1),
                version  INT NOT NULL);
            IF NOT EXISTS (SELECT 1 FROM [{Schema}].[schema_version])
              INSERT INTO [{Schema}].[schema_version] (lock_row, version) VALUES (1, 0);
            """);
    }

    public override DbConnection OpenControl() => OpenRaw();

    /// <summary>
    /// The control store exists once migrations have actually run. Checking the version rather
    /// than the database keeps first-run bootstrap correct when an operator has pre-created an
    /// empty database for Logrr to fill, which is the normal DBA workflow.
    /// </summary>
    public override bool ControlExists()
    {
        try
        {
            using var conn = OpenRaw();
            return Db.Scalar(conn, $"""
                SELECT CASE WHEN OBJECT_ID('{Schema}.users') IS NULL THEN 0 ELSE 1 END;
                """) != 0;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public override IReadOnlyList<string> ControlMigrations => SqlServerControlSchema.Migrations(Schema);

    public override long GetSchemaVersion(DbConnection conn) =>
        Db.Scalar(conn, $"SELECT COALESCE((SELECT version FROM [{Schema}].[schema_version]), 0);");

    public override void SetSchemaVersion(DbConnection conn, long version) =>
        Db.Exec(conn, $"UPDATE [{Schema}].[schema_version] SET version = {version};");

    // ---- Partitions -------------------------------------------------------------------

    public override string PartitionTable(string appId, DateOnly day) =>
        $"[{Schema}].[{PartitionTableName(appId, day)}]";

    /// <summary>The unqualified table name for an app-day. Public for diagnostics and tests.</summary>
    public static string PartitionTableName(string appId, DateOnly day) =>
        $"{TablePrefix(appId)}{day:yyyyMMdd}";

    /// <summary>
    /// Everything before the date stamp. App ids are slugs, so the only character needing a
    /// mapping is <c>-</c>; because a slug can never contain <c>_</c>, that mapping cannot make
    /// two different apps share a table.
    /// </summary>
    private static string TablePrefix(string appId)
    {
        if (!AppIdSlug().IsMatch(appId))
        {
            throw new ArgumentException(
                $"App id '{appId}' cannot be used with the SQL Server backend: ids must match " +
                "^[a-z0-9-]{1,32}$ so they map onto a table name.", nameof(appId));
        }
        return "events_" + appId.Replace('-', '_') + "_";
    }

    public override DbConnection OpenPartitionWriter(
        string appId, DateOnly day, IReadOnlyList<string> indexedProperties)
    {
        var conn = OpenRaw();
        Db.Exec(conn, SqlServerPartitionSchema.BuildDdl(
            Schema, PartitionTableName(appId, day), indexedProperties));
        return conn;
    }

    public override DbConnection? OpenPartitionReader(string appId, DateOnly day)
    {
        var conn = OpenRaw();
        var exists = Db.Scalar(conn, $"""
            SELECT CASE WHEN OBJECT_ID('{Schema}.{PartitionTableName(appId, day)}') IS NULL
                        THEN 0 ELSE 1 END;
            """) != 0;
        if (exists)
        {
            return conn;
        }
        conn.Dispose();
        return null;
    }

    public override IReadOnlyList<DateOnly> PartitionDays(string appId)
    {
        var prefix = TablePrefix(appId);
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name LIKE @prefix + '________' ESCAPE '\';
            """;
        cmd.Add("@schema", Schema);
        cmd.Add("@prefix", EscapeLike(prefix));

        var days = new List<DateOnly>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (name.Length == prefix.Length + 8
                && DateOnly.TryParseExact(name[^8..], "yyyyMMdd", out var day))
            {
                days.Add(day);
            }
        }
        return days;
    }

    public override long PartitionSizeBytes(string appId, DateOnly day)
    {
        using var conn = OpenRaw();
        using var cmd = conn.CreateCommand();
        // Reserved pages rather than used: it is the number the size cap is meant to bound,
        // and it is what the equivalent SQLite file length reports too.
        cmd.CommandText = """
            SELECT COALESCE(SUM(a.total_pages), 0) * 8192
            FROM sys.tables t
            JOIN sys.schemas s     ON s.schema_id = t.schema_id
            JOIN sys.partitions p  ON p.object_id = t.object_id
            JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE s.name = @schema AND t.name = @table;
            """;
        cmd.Add("@schema", Schema);
        cmd.Add("@table", PartitionTableName(appId, day));
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public override bool DeletePartition(string appId, DateOnly day)
    {
        using var conn = OpenRaw();
        var table = PartitionTableName(appId, day);
        if (Db.Scalar(conn, $"SELECT CASE WHEN OBJECT_ID('{Schema}.{table}') IS NULL THEN 0 ELSE 1 END;") == 0)
        {
            return false;
        }
        Db.Exec(conn, $"DROP TABLE [{Schema}].[{table}];");
        return true;
    }

    public override void InsertEvents(
        DbConnection writer, string table, IReadOnlyList<LogEvent> events, long firstId)
    {
        using var tx = writer.BeginTransaction();
        var offset = 0;
        while (offset < events.Count)
        {
            var take = Math.Min(MaxRowsPerInsert, events.Count - offset);
            InsertChunk(writer, tx, table, events, offset, take, firstId + offset);
            offset += take;
        }
        tx.Commit();
    }

    /// <summary>
    /// One multi-row <c>INSERT … VALUES</c>. Row-at-a-time would cost a network round trip per
    /// event, which is the difference between a batch of 500 taking milliseconds and taking
    /// seconds against a server across the LAN.
    /// </summary>
    private static void InsertChunk(
        DbConnection writer, DbTransaction tx, string table,
        IReadOnlyList<LogEvent> events, int offset, int count, long firstId)
    {
        using var cmd = writer.CreateCommand();
        cmd.Transaction = tx;

        var values = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var e = events[offset + i];
            values.Add(
                $"({cmd.AddParam(firstId + i)}, " +
                $"{cmd.AddParam(EventPartition.UnixMicros(e.Timestamp))}, " +
                $"{cmd.AddParam((int)e.Level)}, " +
                $"{cmd.AddParam(e.Template)}, " +
                $"{cmd.AddParam(e.Message)}, " +
                $"{cmd.AddParam(e.Exception)}, " +
                $"{cmd.AddParam(e.EventType)}, " +
                $"{cmd.AddParam(e.TraceId)}, " +
                $"{cmd.AddParam(e.SpanId)}, " +
                $"{cmd.AddParam(e.Source)}, " +
                $"{cmd.AddParam(e.Machine)}, " +
                $"{cmd.AddParam(PropertyValue.ToJson(e.Properties))})");
        }

        cmd.CommandText = $"""
            INSERT INTO {table} (id, ts, level, template, message, exception, event_type,
                                 trace_id, span_id, source, machine, properties)
            VALUES {string.Join(",\n                   ", values)};
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Nothing to checkpoint — the server owns durability. Just release the connection.</summary>
    public override void CheckpointAndClose(DbConnection writer) => writer.Close();

    /// <summary>The data is not on this machine's disk, so the local free-space guard is moot.</summary>
    public override bool SupportsDiskGuard => false;

    public override long FreeBytes() => long.MaxValue;

    // ---- SQL fragments ----------------------------------------------------------------

    public override string LimitClause(int count) =>
        $"OFFSET 0 ROWS FETCH NEXT {count.ToString(CultureInfo.InvariantCulture)} ROWS ONLY";

    public override string LimitClause(string parameterName) =>
        $"OFFSET 0 ROWS FETCH NEXT {parameterName} ROWS ONLY";

    public override string AnyRowsScalar(string table) =>
        $"SELECT CASE WHEN EXISTS(SELECT 1 FROM {table}) THEN 1 ELSE 0 END;";

    public override string CaseInsensitiveEquals(string column, string parameterName) =>
        $"{column} COLLATE Latin1_General_CI_AS = {parameterName}";

    public override string CastToLong(string expression) => $"CAST({expression} AS BIGINT)";

    /// <summary>
    /// SQL Server's full-text search would need a catalog per partition table and populates
    /// asynchronously, which is the wrong shape for a log tail where the newest events must be
    /// searchable immediately — so text search is a <c>LIKE</c> scan instead. It is bounded by
    /// the partition (one app, one day) that the reader has already narrowed to. Whitespace
    /// separates terms and every term must appear, matching how FTS5 reads a bare query;
    /// unlike FTS5 it does not stem, so <c>connect</c> will not find <c>connection</c>.
    /// </summary>
    public override string TextSearchPredicate(DbCommand cmd, string table, string text)
    {
        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries
                                              | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return "1=1";
        }

        var clauses = terms.Select(term =>
        {
            var p = cmd.AddParam("%" + EscapeLike(term) + "%");
            return $"(message LIKE {p} ESCAPE '\\' OR exception LIKE {p} ESCAPE '\\')";
        });
        return "(" + string.Join(" AND ", clauses) + ")";
    }

    /// <summary>Neutralise LIKE metacharacters in a literal being matched.</summary>
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_")
        .Replace("[", "\\[");

    private SqlConnection OpenRaw()
    {
        var conn = new SqlConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
