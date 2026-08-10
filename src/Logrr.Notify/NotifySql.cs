using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

internal static class NotifySql
{
    public static DbParameter P(this DbCommand cmd, string name, object? value) => cmd.Add(name, value);

    public static long Ms(this DateTimeOffset ts) => ts.ToUnixTimeMilliseconds();

    public static long? Ms(this DateTimeOffset? ts) => ts?.ToUnixTimeMilliseconds();

    public static DateTimeOffset ReadTs(this DbDataReader r, string col) =>
        DateTimeOffset.FromUnixTimeMilliseconds(r.Int64(col));

    public static DateTimeOffset? ReadTsNull(this DbDataReader r, string col) =>
        r.LongNull(col) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
