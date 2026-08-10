using Logrr.Storage.Control;
using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

/// <summary>Accumulates per-rule/per-key occurrences for dedupe/threshold/cooldown (SPEC §10.4).</summary>
public sealed class OccurrenceStore(ControlDatabase db)
{
    public Occurrence? Get(string ruleId, string dedupeKey)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM rule_occurrences WHERE rule_id = @r AND dedupe_key = @k;";
        cmd.P("@r", ruleId);
        cmd.P("@k", dedupeKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Upsert(Occurrence o)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = db.Dialect.IsSqlServer
            // Lock-then-insert rather than MERGE: the rule engine upserts the same
            // (rule, dedupe key) from several dispatcher threads, and taking the key-range lock
            // on the probe is what stops two of them racing into a primary-key violation.
            ? """
              BEGIN TRANSACTION;
              UPDATE rule_occurrences WITH (UPDLOCK, SERIALIZABLE)
                SET window_start_utc=@ws, [count]=@count, last_seen_utc=@last,
                    sample_event=@sample, last_fired_utc=@fired, ticket_url=@ticket
                WHERE rule_id=@r AND dedupe_key=@k;
              IF @@ROWCOUNT = 0
                INSERT INTO rule_occurrences (rule_id, dedupe_key, window_start_utc, [count],
                  first_seen_utc, last_seen_utc, sample_event, last_fired_utc, ticket_url)
                VALUES (@r, @k, @ws, @count, @first, @last, @sample, @fired, @ticket);
              COMMIT;
              """
            : """
              INSERT INTO rule_occurrences (rule_id, dedupe_key, window_start_utc, [count],
                first_seen_utc, last_seen_utc, sample_event, last_fired_utc, ticket_url)
              VALUES (@r, @k, @ws, @count, @first, @last, @sample, @fired, @ticket)
              ON CONFLICT(rule_id, dedupe_key) DO UPDATE SET
                window_start_utc=@ws, [count]=@count, last_seen_utc=@last,
                sample_event=@sample, last_fired_utc=@fired, ticket_url=@ticket;
              """;
        cmd.P("@r", o.RuleId);
        cmd.P("@k", o.DedupeKey);
        cmd.P("@ws", o.WindowStartUtc.Ms());
        cmd.P("@count", o.Count);
        cmd.P("@first", o.FirstSeenUtc.Ms());
        cmd.P("@last", o.LastSeenUtc.Ms());
        cmd.P("@sample", o.SampleEvent);
        cmd.P("@fired", o.LastFiredUtc.Ms());
        cmd.P("@ticket", o.TicketUrl);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clear all occurrences for a rule (manual reset-cooldown, SPEC §10.8).</summary>
    public int ResetForRule(string ruleId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rule_occurrences WHERE rule_id = @r;";
        cmd.P("@r", ruleId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Prune stale rollups (SPEC §10.4: after max(cooldown, window) × 3).</summary>
    public int PruneOlderThan(DateTimeOffset cutoff)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rule_occurrences WHERE last_seen_utc < @cutoff;";
        cmd.P("@cutoff", cutoff.Ms());
        return cmd.ExecuteNonQuery();
    }

    private static Occurrence Map(DbDataReader r) => new()
    {
        RuleId = r.GetString(r.GetOrdinal("rule_id")),
        DedupeKey = r.GetString(r.GetOrdinal("dedupe_key")),
        WindowStartUtc = r.ReadTs("window_start_utc"),
        Count = r.Int32("count"),
        FirstSeenUtc = r.ReadTs("first_seen_utc"),
        LastSeenUtc = r.ReadTs("last_seen_utc"),
        SampleEvent = r.Str("sample_event"),
        LastFiredUtc = r.ReadTsNull("last_fired_utc"),
        TicketUrl = r.Str("ticket_url"),
    };
}
