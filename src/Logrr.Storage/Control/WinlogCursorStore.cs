using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>A stored high-water mark, as shown on the collection status page.</summary>
public sealed record WinlogCursor(string Machine, string Channel, long LastRecordId, DateTimeOffset UpdatedUtc);

/// <summary>
/// High-water marks for agentless Windows Event Log collection (SPEC §6.4): the last
/// <c>EventRecordID</c> shipped per (machine, channel). Keys are normalised to lower case
/// because Windows machine and log names are case-insensitive, which keeps the primary key
/// meaningful on both backends without relying on either one's collation.
/// </summary>
public sealed class WinlogCursorStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string Table => db.T("winlog_cursors");

    public long? Get(string machine, string channel)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT last_record_id FROM {Table} WHERE machine = @m AND channel = @c;";
        cmd.Add("@m", Key(machine));
        cmd.Add("@c", Key(channel));
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Every cursor, for the admin status view. The table has one row per collected channel.</summary>
    public IReadOnlyList<WinlogCursor> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT machine, channel, last_record_id, updated_utc
            FROM {Table} ORDER BY machine, channel;
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<WinlogCursor>();
        while (reader.Read())
        {
            list.Add(new WinlogCursor(
                reader.GetString(0),
                reader.GetString(1),
                Convert.ToInt64(reader.GetValue(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader.GetValue(3)))));
        }
        return list;
    }

    public void Set(string machine, string channel, long lastRecordId, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // Single-writer table (only the collector touches it), so the plain update-then-insert
        // upsert is enough on SQL Server and needs no MERGE or lock hint.
        cmd.CommandText = db.Dialect.IsSqlServer
            ? $"""
              UPDATE {Table} SET last_record_id = @r, updated_utc = @u
              WHERE machine = @m AND channel = @c;
              IF @@ROWCOUNT = 0
                INSERT INTO {Table} (machine, channel, last_record_id, updated_utc)
                VALUES (@m, @c, @r, @u);
              """
            : $"""
              INSERT INTO {Table} (machine, channel, last_record_id, updated_utc)
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

    /// <summary>
    /// Forget one channel's high-water mark, so the next poll seeds it again from the tail (or
    /// the backfill window). The recovery hatch for a cursor that has run ahead of the log.
    /// </summary>
    public void Delete(string machine, string channel)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table} WHERE machine = @m AND channel = @c;";
        cmd.Add("@m", Key(machine));
        cmd.Add("@c", Key(channel));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Forget every channel of a machine. Used when a machine is removed from collection, so a
    /// row re-added later starts cleanly rather than resuming a cursor from months ago.
    /// </summary>
    public void DeleteForMachine(string machine)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table} WHERE machine = @m;";
        cmd.Add("@m", Key(machine));
        cmd.ExecuteNonQuery();
    }

    private static string Key(string value) => value.Trim().ToLowerInvariant();
}
