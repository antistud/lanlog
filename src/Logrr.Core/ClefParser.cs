using System.Globalization;
using System.Text.Json;
using Logrr.Contracts;

namespace Logrr.Core;

/// <summary>Outcome of parsing one CLEF line.</summary>
public readonly record struct ClefParseResult(LogEvent? Event, string? Error)
{
    public bool Ok => Event is not null;

    public static ClefParseResult Success(LogEvent e) => new(e, null);
    public static ClefParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Parses newline-delimited CLEF (Compact Log Event Format) — the wire format
/// <c>Serilog.Sinks.Seq</c> emits (SPEC §6.1). Reserved <c>@</c>-fields are lifted onto
/// columns; everything else becomes a property. A <c>@@</c> prefix escapes a literal
/// <c>@</c> property name.
/// </summary>
public static class ClefParser
{
    // Reserved field names (SPEC §6.1).
    private const string T = "@t";   // timestamp
    private const string M = "@m";   // rendered message
    private const string Mt = "@mt"; // template
    private const string L = "@l";   // level
    private const string X = "@x";   // exception
    private const string I = "@i";   // event id
    private const string R = "@r";   // renderings
    private const string Tr = "@tr"; // trace id
    private const string Sp = "@sp"; // span id
    private const string Ps = "@ps"; // parent span id
    private const string St = "@st"; // span start (this event ends a span)

    /// <summary>
    /// Parse one CLEF object. <paramref name="nowUtc"/> is the server clock used when
    /// <c>@t</c> is absent. Never throws for a malformed line — returns an error instead so
    /// the caller can report a partial failure and keep the rest of the batch.
    /// </summary>
    public static ClefParseResult Parse(JsonElement obj, DateTimeOffset nowUtc)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return ClefParseResult.Fail("line is not a JSON object");
        }

        DateTimeOffset ts = nowUtc;
        string? template = null;
        string? rendered = null;
        string? exception = null;
        string? traceId = null;
        string? spanId = null;
        string? parentSpanId = null;
        string? spanStart = null;
        LogLevel level = LevelMap.Default;
        string? rawLevel = null;
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var prop in obj.EnumerateObject())
        {
            var name = prop.Name;
            switch (name)
            {
                case T:
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(prop.Value.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        ts = parsed;
                    }
                    else
                    {
                        return ClefParseResult.Fail("invalid @t");
                    }
                    break;

                case Mt:
                    template = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                    break;

                case M:
                    rendered = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                    break;

                case L:
                    var raw = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                    if (!LevelMap.TryParse(raw, out level))
                    {
                        rawLevel = raw;
                    }
                    break;

                case X:
                    exception = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                    break;

                case Tr:
                    traceId = AsString(prop.Value);
                    break;

                case Sp:
                    spanId = AsString(prop.Value);
                    break;

                case Ps:
                    parentSpanId = AsString(prop.Value);
                    break;

                case St:
                    spanStart = AsString(prop.Value);
                    break;

                case I:
                case R:
                    // Reserved but not stored as first-class columns; drop quietly.
                    break;

                default:
                    // "@@x" escapes to a literal "@x" property (SPEC §6.1).
                    var key = name.StartsWith("@@", StringComparison.Ordinal) ? name[1..] : name;
                    properties[key] = PropertyValue.FromJson(prop.Value);
                    break;
            }
        }

        if (rawLevel is not null)
        {
            properties["_rawLevel"] = rawLevel;
        }

        // Span shape has no columns of its own (see SpanFields) — it travels as properties,
        // which is all the trace view needs to nest spans and measure them.
        if (parentSpanId is not null)
        {
            properties[SpanFields.ParentSpanId] = parentSpanId;
        }
        if (spanStart is not null)
        {
            properties[SpanFields.SpanStart] = spanStart;
        }

        // @m is optional; render @mt server-side when it is absent (SPEC §6.1).
        var message = rendered
            ?? (template is not null ? MessageTemplate.Render(template, properties) : string.Empty);

        var ev = new LogEvent
        {
            Timestamp = ts,
            Level = level,
            Template = template,
            Message = message,
            Exception = exception,
            EventType = EventTypeHash.Compute(template, message),
            TraceId = traceId,
            SpanId = spanId,
            Source = properties.TryGetValue("SourceContext", out var sc) ? sc as string : null,
            Machine = properties.TryGetValue("MachineName", out var mn) ? mn as string : null,
            Properties = properties,
        };

        return ClefParseResult.Success(ev);
    }

    /// <summary>
    /// Reserved fields that are ids, read leniently: a non-string <c>@tr</c> is a malformed
    /// line, and this parser's contract is to never throw on one.
    /// </summary>
    private static string? AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
