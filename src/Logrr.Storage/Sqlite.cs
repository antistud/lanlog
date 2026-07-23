using Microsoft.Data.Sqlite;

namespace Logrr.Storage;

/// <summary>Shared SQLite helpers: connection pragmas and small command conveniences.</summary>
internal static class Sqlite
{
    /// <summary>Connection pragmas from SPEC §4.2.</summary>
    public static void ApplyPragmas(SqliteConnection conn)
    {
        Exec(conn, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -8000;
            """);
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static SqliteParameter Add(this SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>Add an auto-named positional parameter and return its placeholder name.</summary>
    public static string AddParam(this SqliteCommand cmd, object? value)
    {
        var name = "@p" + cmd.Parameters.Count;
        Add(cmd, name, value);
        return name;
    }

    public static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static void SetUserVersion(SqliteConnection conn, long version) =>
        Exec(conn, $"PRAGMA user_version = {version};");
}
