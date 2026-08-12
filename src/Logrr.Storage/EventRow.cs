using System.Data.Common;
using System.Text.Json;
using Logrr.Contracts;

namespace Logrr.Storage;

/// <summary>
/// The event column list and the reader that maps it. They are ordinal-coupled, so they live
/// together rather than at opposite ends of a query — every <c>SELECT</c> that wants a
/// <see cref="LogEventDto"/> selects <see cref="Columns"/> and maps with <see cref="Map"/>.
/// </summary>
internal static class EventRow
{
    public const string Columns =
        "id, ts, level, template, message, exception, event_type, " +
        "trace_id, span_id, source, machine, properties";

    public static LogEventDto Map(DbDataReader r, DateOnly day, long rowid)
    {
        var micros = Convert.ToInt64(r.GetValue(1));
        JsonElement? props = null;
        if (!r.IsDBNull(11))
        {
            var json = r.GetString(11);
            if (!string.IsNullOrEmpty(json))
            {
                props = JsonSerializer.Deserialize<JsonElement>(json);
            }
        }

        return new LogEventDto
        {
            Id = EventId.Format(day, rowid),
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(micros * 10),
            Level = (LogLevel)Convert.ToInt32(r.GetValue(2)),
            Template = r.IsDBNull(3) ? null : r.GetString(3),
            Message = r.GetString(4),
            Exception = r.IsDBNull(5) ? null : r.GetString(5),
            EventType = r.IsDBNull(6) ? null : Convert.ToInt64(r.GetValue(6)),
            TraceId = r.IsDBNull(7) ? null : r.GetString(7),
            SpanId = r.IsDBNull(8) ? null : r.GetString(8),
            Source = r.IsDBNull(9) ? null : r.GetString(9),
            Machine = r.IsDBNull(10) ? null : r.GetString(10),
            Properties = props,
        };
    }
}
