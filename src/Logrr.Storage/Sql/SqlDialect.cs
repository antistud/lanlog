using System.Data.Common;
using Logrr.Core;
using Logrr.Core.Filters;

namespace Logrr.Storage.Sql;

/// <summary>
/// Everything that differs between the two storage backends (SPEC §4.7), behind one seam.
/// The stores, readers and retention loop are written once against this; only the two
/// implementations know about files-and-pragmas versus schemas-and-catalogs.
/// </summary>
/// <remarks>
/// The shape is deliberately partition-oriented rather than a general ORM: Logrr's whole
/// storage model is "an app-day is a unit you create, scan, size and drop whole", and that
/// survives the move to SQL Server intact — a partition is a table there instead of a file,
/// so retention stays a metadata operation (<c>DROP TABLE</c>) rather than a mass delete.
/// </remarks>
public abstract class SqlDialect
{
    public abstract StorageBackend Backend { get; }

    public bool IsSqlServer => Backend == StorageBackend.SqlServer;

    /// <summary>Which SQL flavour the filter compiler should emit for this backend.</summary>
    public abstract FilterSqlDialect FilterSql { get; }

    /// <summary>A one-line description of where data is going, for the startup log.</summary>
    public abstract string Describe();

    // ---- Control database -------------------------------------------------------------

    /// <summary>Create the control database/schema if it does not exist yet.</summary>
    public abstract void EnsureControlCreated();

    /// <summary>Open a connection to the control database, ready to use.</summary>
    public abstract DbConnection OpenControl();

    /// <summary>True once the control store has been initialised — drives first-run bootstrap.</summary>
    public abstract bool ControlExists();

    /// <summary>Ordered control-schema migrations; index + 1 is the resulting version.</summary>
    public abstract IReadOnlyList<string> ControlMigrations { get; }

    public abstract long GetSchemaVersion(DbConnection conn);

    public abstract void SetSchemaVersion(DbConnection conn, long version);

    /// <summary>
    /// A control table as it must be written in a statement. SQL Server resolves an unqualified
    /// name against the connecting login's default schema — normally <c>dbo</c> — so anything
    /// Logrr creates in its own schema has to be named in full or it simply is not found.
    /// </summary>
    public abstract string ControlTable(string table);

    // ---- Partitions -------------------------------------------------------------------

    /// <summary>The table an app-day's events live in, as written in a <c>FROM</c> clause.</summary>
    public abstract string PartitionTable(string appId, DateOnly day);

    /// <summary>
    /// Open (creating and applying DDL if needed) the single writer connection for an app-day.
    /// </summary>
    public abstract DbConnection OpenPartitionWriter(
        string appId, DateOnly day, IReadOnlyList<string> indexedProperties);

    /// <summary>A read-only connection for an existing partition, or null if there is none.</summary>
    public abstract DbConnection? OpenPartitionReader(string appId, DateOnly day);

    /// <summary>Partition days that exist for an app.</summary>
    public abstract IReadOnlyList<DateOnly> PartitionDays(string appId);

    /// <summary>Bytes an app-day occupies, for the size cap and the stats tile.</summary>
    public abstract long PartitionSizeBytes(string appId, DateOnly day);

    /// <summary>Remove a partition whole. Returns true if something was actually removed.</summary>
    public abstract bool DeletePartition(string appId, DateOnly day);

    /// <summary>
    /// Insert a batch on the writer connection, ids assigned from <paramref name="firstId"/>
    /// upward, in one transaction. Any backend-specific index maintenance happens here too.
    /// </summary>
    public abstract void InsertEvents(
        DbConnection writer, string table, IReadOnlyList<LogEvent> events, long firstId);

    /// <summary>Flush and close a writer connection on shutdown. Best-effort.</summary>
    public abstract void CheckpointAndClose(DbConnection writer);

    /// <summary>
    /// Whether the free-space guard applies. It is a property of the local disk, so it means
    /// nothing when the data lives on a SQL Server the app does not own.
    /// </summary>
    public abstract bool SupportsDiskGuard { get; }

    /// <summary>Free bytes on the volume holding the data, or <see cref="long.MaxValue"/>.</summary>
    public abstract long FreeBytes();

    // ---- SQL fragments ----------------------------------------------------------------

    /// <summary>
    /// Row-count limit for a query that already has an <c>ORDER BY</c>, appended last.
    /// </summary>
    public abstract string LimitClause(int count);

    /// <summary>As <see cref="LimitClause(int)"/> but taking the count from a parameter.</summary>
    public abstract string LimitClause(string parameterName);

    /// <summary>A scalar select yielding 1 when the table has any row, else 0.</summary>
    public abstract string AnyRowsScalar(string table);

    /// <summary>A case-insensitive equality predicate for an identifier-like column.</summary>
    public abstract string CaseInsensitiveEquals(string column, string parameterName);

    /// <summary>Cast an expression to a 64-bit integer.</summary>
    public abstract string CastToLong(string expression);

    /// <summary>
    /// Free-text search predicate over an app-day's events, bound onto
    /// <paramref name="cmd"/>. SQLite matches against the partition's FTS5 index; SQL Server
    /// has no equivalent per-partition index and falls back to a <c>LIKE</c> scan, which is
    /// bounded because a query only ever touches one day of one app at a time.
    /// </summary>
    public abstract string TextSearchPredicate(DbCommand cmd, string table, string text);

    // ---- Factory ----------------------------------------------------------------------

    /// <summary>
    /// Build the dialect the options select: SQL Server when a connection string is
    /// configured, SQLite otherwise.
    /// </summary>
    public static SqlDialect Create(StorageOptions options, StoragePaths paths) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new SqliteDialect(paths)
            : new SqlServerDialect(options.ConnectionString, options.Schema);
}
