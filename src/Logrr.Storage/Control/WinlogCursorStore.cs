using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>
/// High-water marks for agentless Windows Event Log collection (SPEC §6.4): the last
/// <c>EventRecordID</c> shipped per (machine, channel). Keys are normalised to lower case
/// because Windows machine and log names are case-insensitive, which keeps the primary key
/// meaningful on both backends without relying on either one's collation.
/// </summary>
public sealed class WinlogCursorStore(ControlDatabase db)
{
    public long? Get(string machine, string channel)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_record_id FROM winlog_cursors WHERE machine = @m AND channel = @c;";
        cmd.Add("@m", Key(machine));
        cmd.Add("@c", Key(channel));
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    public void Set(string machine, string channel, long lastRecordId, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // Single-writer table (only the collector touches it), so the plain update-then-insert
        // upsert is enough on SQL Server and needs no MERGE or lock hint.
        cmd.CommandText = db.Dialect.IsSqlServer
            ? """
              UPDATE winlog_cursors SET last_record_id = @r, updated_utc = @u
              WHERE machine = @m AND channel = @c;
              IF @@ROWCOUNT = 0
                INSERT INTO winlog_cursors (machine, channel, last_record_id, updated_utc)
                VALUES (@m, @c, @r, @u);
              """
            : """
              INSERT INTO winlog_cursors (machine, channel, last_record_id, updated_utc)
              VALUES (@m, @c, @r, @u)
              ON CONFLICT(machine, channel) DO UPDATE SET
                last_record_id = excluded.last_record_id,
                updated_utc    = excluded.updated_utc;
              """;
        cmd.Add("@m", Key(machine));
        cmd.Add("@c", Key(channel));
        cmd.Add("@r", lastRecordId);
        cmd.Add("@u", now.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private static string Key(string value) => value.Trim().ToLowerInvariant();
}
