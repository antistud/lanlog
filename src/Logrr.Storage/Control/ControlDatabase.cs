using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>
/// Owns the control store — <c>control.db</c> on the SQLite backend, the <c>logrr</c> schema on
/// SQL Server: connection factory + the versioned migration runner (SPEC §4.4, §4.7).
/// </summary>
public sealed class ControlDatabase
{
    public ControlDatabase(SqlDialect dialect) => Dialect = dialect;

    /// <summary>Convenience for the default file-backed layout (and for tests).</summary>
    public ControlDatabase(StoragePaths paths) : this(new SqliteDialect(paths))
    {
    }

    public SqlDialect Dialect { get; }

    /// <summary>
    /// A control table named for the active backend — bare on SQLite, schema-qualified on SQL
    /// Server. Every store writes its table names through this, because an unqualified name on
    /// SQL Server resolves against the login's default schema and would miss Logrr's entirely.
    /// </summary>
    public string T(string table) => Dialect.ControlTable(table);

    /// <summary>True before the first run — used to trigger bootstrap (SPEC §2).</summary>
    public bool Exists() => Dialect.ControlExists();

    /// <summary>Open a connection, ready to use.</summary>
    public DbConnection Open() => Dialect.OpenControl();

    /// <summary>Create the store if needed and run any pending migrations.</summary>
    public void Initialize()
    {
        Dialect.EnsureControlCreated();

        using var conn = Open();
        var migrations = Dialect.ControlMigrations;
        var current = Dialect.GetSchemaVersion(conn);
        for (var v = current; v < migrations.Count; v++)
        {
            using (var tx = conn.BeginTransaction())
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migrations[(int)v];
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            Dialect.SetSchemaVersion(conn, v + 1);
        }
    }
}
