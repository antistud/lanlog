using Logrr.Contracts;

namespace Logrr.Notify;

public enum AuthMode { None = 0, Bearer = 1, Basic = 2, HmacSha256 = 3 }

public enum TriggerType { EveryMatch = 0, Threshold = 1 }

public enum DeliverySource { Rule = 0, Manual = 1, Test = 2 }

public enum DeliveryStatus { Pending = 0, Delivered = 1, Failed = 2, DeadLettered = 3 }

/// <summary>A webhook target (SPEC §10.1). Secrets are stored encrypted and never returned.</summary>
public sealed record Destination
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string Method { get; init; } = "POST";
    public string ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public AuthMode AuthMode { get; init; }
    public byte[]? AuthSecret { get; init; }        // encrypted at rest
    public string? AuthHeaderName { get; init; }
    public required string BodyTemplate { get; init; }
    public string? TicketIdPath { get; init; }
    public string? TicketUrlPath { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxAttempts { get; init; } = 6;
    public int RateLimitPerHour { get; init; } = 60;
    public bool IsEnabled { get; init; } = true;
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? CircuitOpenUntilUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }

    public bool IsJson => ContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An alerting rule (SPEC §10.3).</summary>
public sealed record Rule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? AppId { get; init; }             // null = all apps
    public string? Filter { get; init; }
    public LogLevel MinimumLevel { get; init; }
    public TriggerType TriggerType { get; init; }
    public int? ThresholdCount { get; init; }
    public int? ThresholdWindowMinutes { get; init; }
    public string DedupeKeyTemplate { get; init; } = "{{event.eventType}}";
    public int CooldownMinutes { get; init; } = 60;
    public required string DestinationId { get; init; }
    public string? BodyTemplateOverride { get; init; }
    public int MaxFiresPerHour { get; init; } = 20;
    public bool IsDryRun { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? AutoDisabledReason { get; init; }
    public DateTimeOffset? LastFiredUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>A queued outbound webhook (SPEC §10.5).</summary>
public sealed record Delivery
{
    public required string Id { get; init; }
    public required string DestinationId { get; init; }
    public string? RuleId { get; init; }
    public string? AppId { get; init; }
    public DeliverySource Source { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DeliveryStatus Status { get; init; }
    public string? RequestBody { get; init; }
    public int? ResponseStatus { get; init; }
    public string? ResponseSnippet { get; init; }
    public string? TicketId { get; init; }
    public string? TicketUrl { get; init; }
    public string? Error { get; init; }
}

/// <summary>Per-rule, per-key rollup for dedupe/threshold/cooldown (SPEC §10.4).</summary>
public sealed record Occurrence
{
    public required string RuleId { get; init; }
    public required string DedupeKey { get; init; }
    public DateTimeOffset WindowStartUtc { get; init; }
    public int Count { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public string? SampleEvent { get; init; }
    public DateTimeOffset? LastFiredUtc { get; init; }
    public string? TicketUrl { get; init; }
}

/// <summary>A link between an event type and a created ticket (SPEC §10.6).</summary>
public sealed record TicketLink
{
    public required string Id { get; init; }
    public required string AppId { get; init; }
    public long? EventType { get; init; }
    public string? DedupeKey { get; init; }
    public string? EventId { get; init; }
    public string? TicketId { get; init; }
    public string? TicketUrl { get; init; }
    public string? DeliveryId { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
