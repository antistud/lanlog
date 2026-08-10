using Logrr.Storage.Control;
using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

/// <summary>The durable delivery queue (SPEC §10.5). A webhook outage delays, never loses.</summary>
public sealed class DeliveryStore(ControlDatabase db)
{
    public void Enqueue(Delivery d) => Upsert(d);

    public void Update(Delivery d) => Upsert(d);

    private void Upsert(Delivery d)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = db.Dialect.IsSqlServer
            // The same lock-then-insert shape as the other upserts: the dispatcher writes an
            // attempt result while a rule may be enqueuing the same delivery id.
            ? """
              BEGIN TRANSACTION;
              UPDATE deliveries WITH (UPDLOCK, SERIALIZABLE)
                SET attempt=@attempt, next_attempt_utc=@next, status=@status,
                    request_body=@body, subject=@subject, response_status=@respStatus,
                    response_snippet=@respSnippet,
                    ticket_id=@ticketId, ticket_url=@ticketUrl, error=@error
                WHERE id=@id;
              IF @@ROWCOUNT = 0
                INSERT INTO deliveries (id, destination_id, rule_id, app_id, source, event_type,
                  created_utc, attempt, next_attempt_utc, status, request_body, subject,
                  response_status, response_snippet, ticket_id, ticket_url, error)
                VALUES (@id, @dest, @rule, @app, @source, @eventType, @created,
                  @attempt, @next, @status, @body, @subject, @respStatus, @respSnippet,
                  @ticketId, @ticketUrl, @error);
              COMMIT;
              """
            : """
              INSERT INTO deliveries (id, destination_id, rule_id, app_id, source, event_type, created_utc,
                attempt, next_attempt_utc, status, request_body, subject, response_status, response_snippet,
                ticket_id, ticket_url, error)
              VALUES (@id, @dest, @rule, @app, @source, @eventType, @created,
                @attempt, @next, @status, @body, @subject, @respStatus, @respSnippet,
                @ticketId, @ticketUrl, @error)
              ON CONFLICT(id) DO UPDATE SET
                attempt=@attempt, next_attempt_utc=@next, status=@status,
                request_body=@body, subject=@subject, response_status=@respStatus, response_snippet=@respSnippet,
                ticket_id=@ticketId, ticket_url=@ticketUrl, error=@error;
              """;
        cmd.P("@id", d.Id);
        cmd.P("@dest", d.DestinationId);
        cmd.P("@rule", d.RuleId);
        cmd.P("@app", d.AppId);
        cmd.P("@source", (int)d.Source);
        cmd.P("@eventType", d.EventType);
        cmd.P("@created", d.CreatedUtc.Ms());
        cmd.P("@attempt", d.Attempt);
        cmd.P("@next", d.NextAttemptUtc.Ms());
        cmd.P("@status", (int)d.Status);
        cmd.P("@body", d.RequestBody);
        cmd.P("@subject", d.Subject);
        cmd.P("@respStatus", d.ResponseStatus);
        cmd.P("@respSnippet", d.ResponseSnippet);
        cmd.P("@ticketId", d.TicketId);
        cmd.P("@ticketUrl", d.TicketUrl);
        cmd.P("@error", d.Error);
        cmd.ExecuteNonQuery();
    }

    public Delivery? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM deliveries WHERE id = @id;";
        cmd.P("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Pending deliveries whose next-attempt time has arrived (SPEC §10.5).</summary>
    public IReadOnlyList<Delivery> ClaimDue(DateTimeOffset now, int limit)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT * FROM deliveries
            WHERE status = @pending AND (next_attempt_utc IS NULL OR next_attempt_utc <= @now)
            ORDER BY created_utc
            {db.Dialect.LimitClause("@limit")};
            """;
        cmd.P("@pending", (int)DeliveryStatus.Pending);
        cmd.P("@now", now.Ms());
        cmd.P("@limit", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<Delivery>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public IReadOnlyList<Delivery> Query(DeliveryStatus? status, string? destinationId,
        DateTimeOffset? from, DateTimeOffset? to, int limit = 200)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = "1=1";
        if (status is { } s) { where += " AND status = @status"; cmd.P("@status", (int)s); }
        if (destinationId is { } d) { where += " AND destination_id = @dest"; cmd.P("@dest", d); }
        if (from is { } f) { where += " AND created_utc >= @from"; cmd.P("@from", f.Ms()); }
        if (to is { } t) { where += " AND created_utc <= @to"; cmd.P("@to", t.Ms()); }
        cmd.P("@limit", limit);
        cmd.CommandText =
            $"SELECT * FROM deliveries WHERE {where} ORDER BY created_utc DESC " +
            $"{db.Dialect.LimitClause("@limit")};";
        using var r = cmd.ExecuteReader();
        var list = new List<Delivery>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public int CountSince(DateTimeOffset since, string? destinationId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = destinationId is null
            ? "SELECT COUNT(*) FROM deliveries WHERE created_utc >= @since;"
            : "SELECT COUNT(*) FROM deliveries WHERE created_utc >= @since AND destination_id = @dest;";
        cmd.P("@since", since.Ms());
        if (destinationId is not null) cmd.P("@dest", destinationId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountForRuleSince(string ruleId, DateTimeOffset since)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deliveries WHERE rule_id = @r AND created_utc >= @since;";
        cmd.P("@r", ruleId);
        cmd.P("@since", since.Ms());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountByStatus(DeliveryStatus status)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deliveries WHERE status = @s;";
        cmd.P("@s", (int)status);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    internal static Delivery Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        DestinationId = r.GetString(r.GetOrdinal("destination_id")),
        RuleId = r.Str("rule_id"),
        AppId = r.Str("app_id"),
        Source = (DeliverySource)r.Int32("source"),
        EventType = r.LongNull("event_type"),
        CreatedUtc = r.ReadTs("created_utc"),
        Attempt = r.Int32("attempt"),
        NextAttemptUtc = r.ReadTsNull("next_attempt_utc"),
        Status = (DeliveryStatus)r.Int32("status"),
        RequestBody = r.Str("request_body"),
        Subject = r.Str("subject"),
        ResponseStatus = r.IntNull("response_status"),
        ResponseSnippet = r.Str("response_snippet"),
        TicketId = r.Str("ticket_id"),
        TicketUrl = r.Str("ticket_url"),
        Error = r.Str("error"),
    };
}
