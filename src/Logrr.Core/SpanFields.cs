namespace Logrr.Core;

/// <summary>
/// Span metadata that rides in the property bag rather than in columns. The partition DDL is
/// fixed and never migrated (SPEC §4.3), so parent-span and span-start land next to the other
/// server-side markers (<c>_rawLevel</c>, <c>_lateArrival</c>) instead of forcing a schema
/// change on every existing partition. That is enough for the trace waterfall: a span event
/// carries its own start, so its duration is <c>Timestamp - _spanStart</c>.
/// </summary>
public static class SpanFields
{
    /// <summary>Id of the enclosing span (CLEF <c>@ps</c>), for nesting.</summary>
    public const string ParentSpanId = "_parentSpanId";

    /// <summary>
    /// When the span began (CLEF <c>@st</c>), ISO-8601. Its presence is what marks an event
    /// as a completed span rather than a point-in-time log line.
    /// </summary>
    public const string SpanStart = "_spanStart";
}
