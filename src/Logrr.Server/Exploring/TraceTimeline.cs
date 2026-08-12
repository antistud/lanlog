using System.Globalization;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core;

namespace Logrr.Server.Exploring;

/// <summary>One row of the trace waterfall: an event, its place in the span tree, and the
/// geometry of its bar as a percentage of the whole trace.</summary>
public sealed record TraceRow(
    string AppId,
    LogEventDto Event,
    int Depth,
    DateTimeOffset Start,
    TimeSpan Offset,
    TimeSpan? Duration,
    double LeftPercent,
    double WidthPercent)
{
    /// <summary>A completed span (it knows when it began), as opposed to a point-in-time log.</summary>
    public bool IsSpan => Duration is not null;
}

/// <summary>
/// Turns a flat trace into the nested, time-positioned rows the trace screen draws.
/// </summary>
/// <remarks>
/// Span structure is best-effort by design. Apps that emit spans (<c>@sp</c>, <c>@ps</c>,
/// <c>@st</c> — see <see cref="SpanFields"/>) get a real waterfall: nesting from parent span
/// ids, bar widths from span durations, log lines hung under the span they were written in.
/// Apps that emit none of it — the common case for a plain <c>ILogger</c> app that only
/// propagates a trace id — degrade to a flat, chronological list with a tick per event, which
/// is still the thing being asked for: one request's log lines, in order, in one place.
/// </remarks>
public sealed class TraceTimeline
{
    private TraceTimeline(IReadOnlyList<TraceRow> rows, DateTimeOffset start, DateTimeOffset end)
    {
        Rows = rows;
        Start = start;
        End = end;
    }

    public IReadOnlyList<TraceRow> Rows { get; }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public int SpanCount => Rows.Count(r => r.IsSpan);

    public int AppCount => Rows.Select(r => r.AppId).Distinct(StringComparer.Ordinal).Count();

    public int ErrorCount => Rows.Count(r => r.Event.Level >= Contracts.LogLevel.Error);

    public static TraceTimeline Empty { get; } = new([], default, default);

    public static TraceTimeline Build(TraceResponse trace)
    {
        var nodes = trace.Events.Select(e => new Node(e)).ToList();
        if (nodes.Count == 0)
        {
            return Empty;
        }

        var start = nodes.Min(n => n.Start);
        var end = nodes.Max(n => n.Event.Event.Timestamp);
        if (end < start)
        {
            end = start;
        }

        // A trace that happened in an instant still needs a denominator.
        var total = end - start;
        var span = total > TimeSpan.Zero ? total.TotalMilliseconds : 1d;

        Link(nodes);

        var rows = new List<TraceRow>(nodes.Count);
        foreach (var root in Ordered(nodes.Where(n => n.Parent is null)))
        {
            Emit(root, depth: 0, start, span, rows);
        }

        return new TraceTimeline(rows, start, end);
    }

    /// <summary>
    /// Hang each event off the span it belongs to: a span under its parent span, a log line
    /// under the span it was written in. Anything whose parent is missing from this trace —
    /// aged out, outside the scan window, or never logged — becomes a root rather than
    /// disappearing.
    /// </summary>
    private static void Link(List<Node> nodes)
    {
        var spans = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.IsSpan && n.Event.Event.SpanId is { Length: > 0 } id)
            {
                spans.TryAdd(id, n);
            }
        }

        foreach (var n in nodes)
        {
            var parentKey = n.IsSpan ? n.ParentSpanId : n.Event.Event.SpanId;
            if (parentKey is { Length: > 0 } key && spans.TryGetValue(key, out var parent) && parent != n)
            {
                n.Parent = parent;
            }
        }

        // Ids are supplied by the emitting app, so a cycle is possible; re-root rather than
        // recurse forever.
        foreach (var n in nodes)
        {
            if (IsCyclic(n, nodes.Count))
            {
                n.Parent = null;
            }
        }

        foreach (var n in nodes)
        {
            n.Parent?.Children.Add(n);
        }
    }

    private static bool IsCyclic(Node node, int budget)
    {
        for (var walk = node.Parent; walk is not null; walk = walk.Parent)
        {
            if (walk == node || budget-- <= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static void Emit(Node node, int depth, DateTimeOffset traceStart, double spanMs, List<TraceRow> rows)
    {
        var offset = node.Start - traceStart;
        var left = Math.Clamp(offset.TotalMilliseconds / spanMs * 100d, 0d, 100d);
        var width = node.Duration is { } d
            ? Math.Clamp(d.TotalMilliseconds / spanMs * 100d, 0d, 100d - left)
            : 0d;

        rows.Add(new TraceRow(
            node.Event.AppId, node.Event.Event, depth, node.Start, offset, node.Duration, left, width));

        foreach (var child in Ordered(node.Children))
        {
            Emit(child, depth + 1, traceStart, spanMs, rows);
        }
    }

    private static IEnumerable<Node> Ordered(IEnumerable<Node> nodes) => nodes
        .OrderBy(n => n.Start)
        .ThenBy(n => n.Event.Event.Timestamp)
        .ThenBy(n => n.Event.AppId, StringComparer.Ordinal)
        .ThenBy(n => n.Event.Event.Id, StringComparer.Ordinal);

    private sealed class Node
    {
        public Node(TraceEntryDto entry)
        {
            Event = entry;
            ParentSpanId = Property(entry.Event, SpanFields.ParentSpanId);

            // An event that carries its own start is a span that has just ended; the row
            // covers [start, timestamp]. A clock skew that inverts the two is not a duration.
            var began = ParseTime(Property(entry.Event, SpanFields.SpanStart));
            if (began is { } from && from <= entry.Event.Timestamp)
            {
                Start = from;
                Duration = entry.Event.Timestamp - from;
            }
            else
            {
                Start = entry.Event.Timestamp;
            }
        }

        public TraceEntryDto Event { get; }

        public string? ParentSpanId { get; }

        public DateTimeOffset Start { get; }

        public TimeSpan? Duration { get; }

        public bool IsSpan => Duration is not null;

        public Node? Parent { get; set; }

        public List<Node> Children { get; } = [];
    }

    private static string? Property(LogEventDto e, string name) =>
        e.Properties is { ValueKind: JsonValueKind.Object } props
        && props.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
