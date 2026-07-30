using System.Globalization;

namespace Logrr.Storage.Control;

/// <summary>
/// An acknowledgement of errors up to a point in time. Errors at or before
/// <paramref name="ThroughTs"/> (unix micros, matching the <c>events.ts</c> column) count as
/// handled and stop raising the overview alert; anything newer alerts again.
/// </summary>
public sealed record Ack(
    string AppId, string Scope, long ThroughTs, string? Note, string? AckedBy, DateTimeOffset CreatedUtc)
{
    /// <summary>Scope covering every error in the app, whatever its event type.</summary>
    public const string AllScope = "*";

    public bool IsAppWide => Scope == AllScope;

    /// <summary>The scope string for an event type, or <see cref="AllScope"/> when it has none.</summary>
    public static string ScopeFor(long? eventType) =>
        eventType is { } t ? t.ToString(CultureInfo.InvariantCulture) : AllScope;
}

/// <summary>The acks for one app, shaped for the stats query.</summary>
/// <param name="AppWideThroughTs">App-wide watermark, or 0 when nothing is acked app-wide.</param>
/// <param name="ByEventType">Per-event-type watermarks.</param>
public sealed record AckSnapshot(long AppWideThroughTs, IReadOnlyDictionary<long, long> ByEventType)
{
    public static readonly AckSnapshot Empty = new(0, new Dictionary<long, long>());

    public bool IsEmpty => AppWideThroughTs == 0 && ByEventType.Count == 0;

    /// <summary>Whether an error at <paramref name="ts"/> (unix micros) is already acknowledged.</summary>
    public bool Covers(long? eventType, long ts) =>
        ts <= AppWideThroughTs
        || (eventType is { } t && ByEventType.TryGetValue(t, out var through) && ts <= through);
}

/// <summary>CRUD for the <c>acks</c> table (SPEC §7 overview alert).</summary>
public sealed class AckStore(ControlDatabase db)
{
    public IReadOnlyList<Ack> ListByApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT app_id, scope, through_ts, note, acked_by, created_utc FROM acks " +
            "WHERE app_id = $a ORDER BY created_utc DESC;";
        cmd.Add("$a", appId);
        using var reader = cmd.ExecuteReader();
        var list = new List<Ack>();
        while (reader.Read())
        {
            list.Add(new Ack(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        }
        return list;
    }

    public Ack? Find(string appId, string scope) =>
        ListByApp(appId).FirstOrDefault(a => a.Scope == scope);

    /// <summary>The app's acks collapsed into the form <see cref="StatsReader"/> queries with.</summary>
    public AckSnapshot Snapshot(string appId)
    {
        long appWide = 0;
        var byType = new Dictionary<long, long>();
        foreach (var ack in ListByApp(appId))
        {
            if (ack.IsAppWide)
            {
                appWide = Math.Max(appWide, ack.ThroughTs);
            }
            else if (long.TryParse(ack.Scope, NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            {
                byType[type] = Math.Max(byType.GetValueOrDefault(type), ack.ThroughTs);
            }
        }
        return appWide == 0 && byType.Count == 0 ? AckSnapshot.Empty : new AckSnapshot(appWide, byType);
    }

    /// <summary>
    /// Record an acknowledgement. Re-acking a scope only ever moves the watermark forward, so a
    /// stale request can never un-acknowledge errors that were already cleared.
    /// </summary>
    public void Acknowledge(Ack ack)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO acks (app_id, scope, through_ts, note, acked_by, created_utc)
            VALUES ($a, $s, $t, $n, $by, $c)
            ON CONFLICT (app_id, scope) DO UPDATE SET
              through_ts = MAX(acks.through_ts, excluded.through_ts),
              note = excluded.note, acked_by = excluded.acked_by, created_utc = excluded.created_utc;
            """;
        cmd.Add("$a", ack.AppId);
        cmd.Add("$s", ack.Scope);
        cmd.Add("$t", ack.ThroughTs);
        cmd.Add("$n", ack.Note);
        cmd.Add("$by", ack.AckedBy);
        cmd.Add("$c", ack.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Undo an acknowledgement, bringing its errors back into the alert count.</summary>
    public void Clear(string appId, string scope)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM acks WHERE app_id = $a AND scope = $s;";
        cmd.Add("$a", appId);
        cmd.Add("$s", scope);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Undo every acknowledgement for an app.</summary>
    public void ClearAll(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM acks WHERE app_id = $a;";
        cmd.Add("$a", appId);
        cmd.ExecuteNonQuery();
    }
}
