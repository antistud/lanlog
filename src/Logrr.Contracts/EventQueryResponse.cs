namespace Logrr.Contracts;

/// <summary>
/// Result page for the query API (SPEC §7). Paging is cursor-based; no OFFSET anywhere.
/// </summary>
public sealed record EventQueryResponse
{
    public IReadOnlyList<LogEventDto> Events { get; init; } = [];

    /// <summary>
    /// Opaque continuation encoding {partition date, last rowid}; null when exhausted.
    /// </summary>
    public string? NextCursor { get; init; }

    public int PartitionsScanned { get; init; }
}
