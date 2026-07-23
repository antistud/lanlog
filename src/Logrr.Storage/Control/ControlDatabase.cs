using Microsoft.Data.Sqlite;

namespace Logrr.Storage.Control;

/// <summary>
/// Owns <c>control.db</c>: connection factory + the versioned migration runner (SPEC §4.4).
/// </summary>
public sealed class ControlDatabase
{
    private readonly string _connectionString;

    public ControlDatabase(StoragePaths paths)
    {
        Paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ControlDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public StoragePaths Paths { get; }

    /// <summary>True before the first run — used to trigger bootstrap (SPEC §2).</summary>
    public bool Exists() => File.Exists(Paths.ControlDbPath);

    /// <summary>Open a pooled connection with pragmas applied.</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Sqlite.ApplyPragmas(conn);
        return conn;
    }

    /// <summary>Create the data root and run any pending migrations.</summary>
    public void Initialize()
    {
        Paths.EnsureRootDirectories();
        using var conn = Open();
        var current = Sqlite.GetUserVersion(conn);
        for (var v = current; v < ControlSchema.Migrations.Count; v++)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ControlSchema.Migrations[(int)v];
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            Sqlite.SetUserVersion(conn, v + 1);
        }
    }
}
