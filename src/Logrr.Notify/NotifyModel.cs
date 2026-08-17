using Logrr.Contracts;

namespace Logrr.Notify;

public enum AuthMode { None = 0, Bearer = 1, Basic = 2, HmacSha256 = 3 }

/// <summary>How a destination delivers: an HTTP webhook (default) or email over SMTP.</summary>
public enum DestinationKind { Webhook = 0, Smtp = 1 }

/// <summary>SMTP transport security. StartTls (587) is the modern default; SslOnConnect is 465.</summary>
public enum SmtpSecurity { None = 0, StartTls = 1, SslOnConnect = 2 }

public enum TriggerType { EveryMatch = 0, Threshold = 1 }

public enum DeliverySource { Rule = 0, Manual = 1, Test = 2 }

public enum DeliveryStatus { Pending = 0, Delivered = 1, Failed = 2, DeadLettered = 3 }

/// <summary>
/// A notification target (SPEC §10.1). Delivers either as an HTTP webhook or as email over
/// SMTP, per <see cref="Kind"/>. Secrets (webhook auth secret / SMTP password) share the
/// encrypted <see cref="AuthSecret"/> column and are never returned.
/// </summary>
public sealed record Destination
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DestinationKind Kind { get; init; } = DestinationKind.Webhook;

    // --- Webhook ---
    public required string Url { get; init; }
    public string Method { get; init; } = "POST";
    public string ContentType { get; init; } = "application/json";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public AuthMode AuthMode { get; init; }
    public byte[]? AuthSecret { get; init; }        // encrypted at rest (webhook secret OR SMTP password)
    public string? AuthHeaderName { get; init; }
    public string? TicketIdPath { get; init; }
    public string? TicketUrlPath { get; init; }

    // --- SMTP (Kind == Smtp) ---
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public SmtpSecurity SmtpSecurity { get; init; } = SmtpSecurity.StartTls;
    public string? SmtpUsername { get; init; }      // password is AuthSecret
    public string? SmtpFrom { get; init; }
    public string? SmtpTo { get; init; }            // comma/semicolon-separated recipients
    public string SmtpSubjectTemplate { get; init; } = "[{{app.name}}] {{event.level}}: {{event.message}}";

    // --- Shared delivery policy ---
    public required string BodyTemplate { get; init; }
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxAttempts { get; init; } = 6;
    public int RateLimitPerHour { get; init; } = 60;
    public bool IsEnabled { get; init; } = true;
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? CircuitOpenUntilUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Webhook bodies are JSON-escaped when the content type is JSON; email bodies never are.</summary>
    public bool IsJson => Kind == DestinationKind.Webhook
        && ContentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Email body is HTML when the content type says so; otherwise plain text.</summary>
    public bool IsHtmlEmail => ContentType.Contains("html", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// When the rule was last created or edited. Events older than this never fire it, so
    /// widening a rule (lowering the level, loosening the filter) does not retroactively alert
    /// on the backlog still inside the backfill window (SPEC §10.7). Null on rules that predate
    /// the column — those keep the old behaviour until their next edit.
    /// </summary>
    public DateTimeOffset? ScopeChangedUtc { get; init; }

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
    /// <summary>Event-type hash of the triggering event, propagated to the ticket link.</summary>
    public long? EventType { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public DeliveryStatus Status { get; init; }
    public string? RequestBody { get; init; }
    /// <summary>Rendered email subject (SMTP destinations only).</summary>
    public string? Subject { get; init; }
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
