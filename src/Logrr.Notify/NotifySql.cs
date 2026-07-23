using Microsoft.Data.Sqlite;

namespace Logrr.Notify;

internal static class NotifySql
{
    public static SqliteParameter P(this SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
        return p;
    }

    public static long Ms(this DateTimeOffset ts) => ts.ToUnixTimeMilliseconds();

    public static long? Ms(this DateTimeOffset? ts) => ts?.ToUnixTimeMilliseconds();

    public static DateTimeOffset ReadTs(this SqliteDataReader r, string col) =>
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal(col)));

    public static DateTimeOffset? ReadTsNull(this SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(ord));
    }

    public static string? Str(this SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    public static int? IntNull(this SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetInt32(ord);
    }

    public static long? LongNull(this SqliteDataReader r, string col)
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }
}
