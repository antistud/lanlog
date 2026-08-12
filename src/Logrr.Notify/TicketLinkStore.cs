using System.Data.Common;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

/// <summary>Links between event types and created tickets, for grid badges (SPEC §10.6).</summary>
public sealed class TicketLinkStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string Table => db.T("ticket_links");

    public void Create(TicketLink link)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Table} (id, app_id, event_type, dedupe_key, event_id,
              ticket_id, ticket_url, delivery_id, created_by, created_utc)
            VALUES (@id, @app, @type, @dedupe, @event, @ticketId, @ticketUrl, @delivery, @by, @created);
            """;
        cmd.P("@id", link.Id);
        cmd.P("@app", link.AppId);
        cmd.P("@type", link.EventType);
        cmd.P("@dedupe", link.DedupeKey);
        cmd.P("@event", link.EventId);
        cmd.P("@ticketId", link.TicketId);
        cmd.P("@ticketUrl", link.TicketUrl);
        cmd.P("@delivery", link.DeliveryId);
        cmd.P("@by", link.CreatedBy);
        cmd.P("@created", link.CreatedUtc.Ms());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TicketLink> ListByApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} WHERE app_id = @app ORDER BY created_utc DESC;";
        cmd.P("@app", appId);
        using var r = cmd.ExecuteReader();
        var list = new List<TicketLink>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>
    /// Map of event-type → most-recent ticket url for an app, for rendering grid badges
    /// (SPEC §10.6). Loaded once per page, checked in memory per row.
    /// </summary>
    public IReadOnlyDictionary<long, string> TicketUrlsByEventType(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT event_type, ticket_url FROM {Table}
            WHERE app_id = @app AND event_type IS NOT NULL AND ticket_url IS NOT NULL
            ORDER BY created_utc;
            """;
        cmd.P("@app", appId);
        var map = new Dictionary<long, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[Convert.ToInt64(r.GetValue(0))] = r.GetString(1); // later rows overwrite → newest wins
        }
        return map;
    }

    public TicketLink? FindByEventType(string appId, long eventType)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT * FROM {Table} WHERE app_id = @app AND event_type = @type
            ORDER BY created_utc DESC {db.Dialect.LimitClause(1)};
            """;
        cmd.P("@app", appId);
        cmd.P("@type", eventType);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static TicketLink Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        AppId = r.GetString(r.GetOrdinal("app_id")),
        EventType = r.LongNull("event_type"),
        DedupeKey = r.Str("dedupe_key"),
        EventId = r.Str("event_id"),
        TicketId = r.Str("ticket_id"),
        TicketUrl = r.Str("ticket_url"),
        DeliveryId = r.Str("delivery_id"),
        CreatedBy = r.Str("created_by"),
        CreatedUtc = r.ReadTs("created_utc"),
    };
}
