using System.Text.Json;

namespace Logrr.Contracts;

/// <summary>
/// The deliberately-dumb plain-JSON ingest shape (SPEC §6.2), callable from a
/// 15-line VB.NET helper. All fields optional except a message.
/// </summary>
public sealed record PlainEventDto
{
    /// <summary>Optional; server clock is used when absent.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Level name (case-insensitive) or number; unrecognised → Information.</summary>
    public string? Level { get; init; }

    public string? Message { get; init; }

    public string? Exception { get; init; }

    public string? Template { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? Source { get; init; }

    public JsonElement? Properties { get; init; }
}
