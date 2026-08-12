using System.Data;
using System.Data.Common;

namespace Logrr.Storage.Sql;

/// <summary>
/// Provider-agnostic ADO.NET conveniences. Every store binds through these rather than a
/// concrete <c>SqliteCommand</c>/<c>SqlCommand</c> so the same SQL-building code serves both
/// backends (SPEC §4.7).
/// </summary>
public static class Db
{
    /// <summary>
    /// Add a named parameter. Names are always <c>@</c>-prefixed: SQL Server accepts nothing
    /// else, and Microsoft.Data.Sqlite accepts <c>@</c> alongside its own <c>$</c>.
    /// </summary>
    public static DbParameter Add(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>
    /// Add a binary parameter, typed even when the value is null.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="DBNull"/> carries no type, and SqlClient then infers <c>nvarchar</c>
    /// for it — which SQL Server refuses to assign to a <c>varbinary</c> column ("implicit
    /// conversion … is not allowed"). SQLite does not care, so an untyped null only fails on one
    /// backend, and only when the column happens to be null: exactly the sort of bug that hides
    /// until an operator saves a destination with no secret.
    /// </remarks>
    public static DbParameter AddBinary(this DbCommand cmd, string name, byte[]? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = DbType.Binary;
        p.Value = (object?)value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    /// <summary>Add an auto-named positional parameter and return its placeholder name.</summary>
    public static string AddParam(this DbCommand cmd, object? value)
    {
        var name = "@p" + cmd.Parameters.Count;
        Add(cmd, name, value);
        return name;
    }

    public static void Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public static string? Str(this DbDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    public static int? IntNull(this DbDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt32(ord);
    }

    public static long? LongNull(this DbDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }

    /// <summary>
    /// Read an integer column tolerantly. SQLite hands back whatever width it stored, while
    /// SQL Server is strict about <c>INT</c> vs <c>BIGINT</c> — going through the boxed value
    /// keeps one mapping function working against both.
    /// </summary>
    public static int Int32(this DbDataReader r, string column) =>
        Convert.ToInt32(r.GetValue(r.GetOrdinal(column)));

    public static long Int64(this DbDataReader r, string column) =>
        Convert.ToInt64(r.GetValue(r.GetOrdinal(column)));

    public static bool Bool(this DbDataReader r, string column) => Int32(r, column) != 0;

    public static byte[]? Bytes(this DbDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : (byte[])r.GetValue(ord);
    }
}
