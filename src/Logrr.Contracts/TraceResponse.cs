namespace Logrr.Contracts;

/// <summary>One event of a trace, tagged with the app that logged it.</summary>
public sealed record TraceEntryDto
{
    public required string AppId { get; init; }

    public required LogEventDto Event { get; init; }
}

/// <summary>
/// Every event carrying one trace id, oldest first, across every app that was scanned
/// (SPEC §7). A trace is not scoped to an app: the whole point is seeing the web tier and
/// the worker's half of the same request in one list.
/// </summary>
public sealed record TraceResponse
{
    public required string TraceId { get; init; }

    /// <summary>Oldest first — the order a trace is read in.</summary>
    public required IReadOnlyList<TraceEntryDto> Events { get; init; }

    /// <summary>True when the limit cut the trace short; the events dropped are the newest.</summary>
    public bool Truncated { get; init; }

    public int PartitionsScanned { get; init; }
}
