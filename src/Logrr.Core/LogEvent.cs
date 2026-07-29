using Logrr.Contracts;

namespace Logrr.Core;

/// <summary>
/// The internal, hot-path representation of a log event: the shape that flows through the
/// ingest channel, batch writer, realtime broker, and rule engine.
/// </summary>
/// <remarks>
/// Property values are normalised to scalars (<see cref="string"/>, <see cref="double"/>,
/// <see cref="long"/>, <see cref="bool"/>, or <c>null</c>) or, for complex values, a JSON
/// string. Keeping them as plain CLR objects lets the compiled filter predicate and the
/// rule engine compare without touching <c>JsonElement</c>. Server-injected markers such
/// as <c>_lateArrival</c>, <c>_truncated</c>, and <c>_rawLevel</c> live in the same bag.
/// </remarks>
public sealed class LogEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    public required LogLevel Level { get; init; }

    public string? Template { get; init; }

    public required string Message { get; init; }

    public string? Exception { get; init; }

    /// <summary>Message-template hash for grouping + dedupe (see <see cref="EventTypeHash"/>).</summary>
    public long EventType { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Source { get; init; }

    public string? Machine { get; init; }

    public IReadOnlyDictionary<string, object?> Properties { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Look up a comparable value by the identifier used in a filter expression. Built-in
    /// identifiers resolve to their column; anything else falls through to a property.
    /// </summary>
    public object? Resolve(string ident)
    {
        if (Filters.FilterIdent.IsProperty(ident))
        {
            return Properties.TryGetValue(Filters.FilterIdent.PropertyName(ident), out var p) ? p : null;
        }
        return ident switch
        {
            "Level" => (long)(int)Level,
            "Message" => Message,
            "Exception" => Exception,
            "Source" => Source,
            "TraceId" => TraceId,
            "SpanId" => SpanId,
            "Machine" => Machine,
            _ => Properties.TryGetValue(ident, out var v) ? v : null,
        };
    }
}
