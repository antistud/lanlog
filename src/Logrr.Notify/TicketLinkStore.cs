using Logrr.Storage.Control;
using Microsoft.Data.Sqlite;

namespace Logrr.Notify;

/// <summary>Links between event types and created tickets, for grid badges (SPEC §10.6).</summary>
public sealed class TicketLinkStore(ControlDatabase db)
{
    public void Create(TicketLink link)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ticket_links (id, app_id, event_type, dedupe_key, event_id,
              ticket_id, ticket_url, delivery_id, created_by, created_utc)
            VALUES ($id, $app, $type, $dedupe, $event, $ticketId, $ticketUrl, $delivery, $by, $created);
            """;
        cmd.P("$id", link.Id);
        cmd.P("$app", link.AppId);
        cmd.P("$type", link.EventType);
        cmd.P("$dedupe", link.DedupeKey);
        cmd.P("$event", link.EventId);
        cmd.P("$ticketId", link.TicketId);
        cmd.P("$ticketUrl", link.TicketUrl);
        cmd.P("$delivery", link.DeliveryId);
        cmd.P("$by", link.CreatedBy);
        cmd.P("$created", link.CreatedUtc.Ms());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TicketLink> ListByApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ticket_links WHERE app_id = $app ORDER BY created_utc DESC;";
        cmd.P("$app", appId);
        using var r = cmd.ExecuteReader();
        var list = new List<TicketLink>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public TicketLink? FindByEventType(string appId, long eventType)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM ticket_links WHERE app_id = $app AND event_type = $type
            ORDER BY created_utc DESC LIMIT 1;
            """;
        cmd.P("$app", appId);
        cmd.P("$type", eventType);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static TicketLink Map(SqliteDataReader r) => new()
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
