using System.Text.Json;
using Logrr.Core;

namespace Logrr.Notify;

/// <summary>
/// Everything a body template can reference (SPEC §10.2). Fields left null resolve to an
/// empty token so a template never blows up on a missing value.
/// </summary>
public sealed record WebhookContext
{
    public LogEvent? Event { get; init; }
    public string? EventId { get; init; }
    public string? AppId { get; init; }
    public string? AppName { get; init; }
    public Rule? Rule { get; init; }
    public Occurrence? Occurrence { get; init; }
    public string? UserName { get; init; }
    public string? Title { get; init; }   // manual submissions
    public string? Body { get; init; }    // manual submissions
    public string BaseUrl { get; init; } = "https://logrr.internal";
}

/// <summary>Result of rendering: the body plus whether it is valid for its content type.</summary>
public readonly record struct RenderResult(string Body, bool Ok, string? Error);

/// <summary>
/// Renders a destination body template against a <see cref="WebhookContext"/> and validates
/// the result parses as JSON before dispatch (SPEC §10.2) — no posting garbage.
/// </summary>
public static class WebhookRenderer
{
    public static RenderResult Render(string template, WebhookContext ctx, bool jsonMode)
    {
        var body = WebhookTemplate.Render(template, path => Resolve(path, ctx), jsonMode);

        if (jsonMode)
        {
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return new RenderResult(body, false, $"rendered body is not valid JSON: {ex.Message}");
            }
        }

        return new RenderResult(body, true, null);
    }

    private static string? Resolve(string path, WebhookContext ctx)
    {
        var e = ctx.Event;

        if (path.StartsWith("event.properties.", StringComparison.Ordinal))
        {
            var name = path["event.properties.".Length..];
            return e is not null && e.Properties.TryGetValue(name, out var v)
                ? PropertyValue.ToInvariantString(v)
                : null;
        }

        return path switch
        {
            "event.timestamp" => e?.Timestamp.ToString("O"),
            "event.level" => e is null ? null : LevelMap.ToName(e.Level),
            "event.message" => e?.Message,
            "event.template" => e?.Template,
            "event.exception" => e?.Exception,
            "event.source" => e?.Source,
            "event.traceId" => e?.TraceId,
            "event.id" => ctx.EventId,
            "event.eventType" => e?.EventType.ToString(),

            "app.id" => ctx.AppId,
            "app.name" => ctx.AppName,

            "rule.name" => ctx.Rule?.Name,
            "rule.id" => ctx.Rule?.Id,

            "occurrence.count" => ctx.Occurrence?.Count.ToString(),
            "occurrence.firstSeen" => ctx.Occurrence?.FirstSeenUtc.ToString("O"),
            "occurrence.lastSeen" => ctx.Occurrence?.LastSeenUtc.ToString("O"),
            "occurrence.key" => ctx.Occurrence?.DedupeKey,

            "link.event" => ctx.AppId is not null && ctx.EventId is not null
                ? $"{ctx.BaseUrl}/apps/{ctx.AppId}/events/{ctx.EventId}"
                : null,
            "link.search" => ctx.AppId is not null && e is not null
                ? $"{ctx.BaseUrl}/apps/{ctx.AppId}/search?eventType={e.EventType}"
                : null,

            "user.name" => ctx.UserName,
            "title" => ctx.Title,
            "body" => ctx.Body,

            _ => null,
        };
    }
}
