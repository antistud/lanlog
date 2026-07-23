using System.Text.Json;

namespace Logrr.Contracts;

/// <summary>
/// An event as returned by the query API and pushed over the realtime hub (SPEC §7, §8.3).
/// </summary>
public sealed record LogEventDto
{
    /// <summary>Opaque id, "{partitionDate}:{rowid}" — unique across partitions.</summary>
    public required string Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required LogLevel Level { get; init; }

    public required string Message { get; init; }

    public string? Template { get; init; }

    public string? Exception { get; init; }

    /// <summary>Message-template hash used for grouping and dedupe.</summary>
    public long? EventType { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Source { get; init; }

    public string? Machine { get; init; }

    /// <summary>Structured properties, preserved verbatim.</summary>
    public JsonElement? Properties { get; init; }
}
