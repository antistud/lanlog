namespace Logrr.Contracts;

/// <summary>
/// Ingest response body (SPEC §6.1). Partial failures are reported, never a whole-batch
/// rejection over one bad line.
/// </summary>
public sealed record IngestResult
{
    public int Accepted { get; init; }

    public int Rejected { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static IngestResult Empty { get; } = new();
}
