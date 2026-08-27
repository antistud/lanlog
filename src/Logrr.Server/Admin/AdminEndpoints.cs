using Logrr.Contracts;
using Logrr.Core;
using Logrr.Core.Filters;
using Logrr.Notify;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authorization;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Server.Admin;

/// <summary>
/// Management API (SPEC §7, §10.8). Admin-only except manual ticket creation, which a User
/// may perform for apps they can read.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1").RequireAuthorization("Admin");

        // ---- Apps ----
        admin.MapPost("/apps", (CreateAppRequest req, AppStore apps) =>
        {
            if (!SlugOk(req.Id))
            {
                return Results.BadRequest(new { error = "id must match [a-z0-9-]{3,32}" });
            }
            if (apps.Get(req.Id) is not null)
            {
                return Results.Conflict(new { error = "app already exists" });
            }
            apps.Create(new AppRecord
            {
                Id = req.Id,
                Name = req.Name,
                Description = req.Description,
                RetentionDays = req.RetentionDays ?? 14,
                MaxSizeMb = req.MaxSizeMb ?? 2048,
                MinimumLevel = req.MinimumLevel ?? LogLevel.Verbose,
                IndexedProperties = (req.IndexedProperties ?? []).Take(8).ToList(),
                IsEnabled = true,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return Results.Created($"/api/v1/apps/{req.Id}", new { id = req.Id });
        });

        // Deletes the app's events along with it — there is no undo and no export step.
        admin.MapDelete("/apps/{appId}", (string appId, AppDeleter deleter) =>
        {
            var result = deleter.Delete(appId);
            return result is null ? Results.NotFound(new { error = "app not found" }) : Results.Ok(result);
        });

        // ---- Tokens (secret shown once) ----
        admin.MapPost("/apps/{appId}/tokens", (string appId, CreateTokenRequest req, AppStore apps, TokenStore tokens) =>
        {
            if (apps.Get(appId) is null)
            {
                return Results.NotFound(new { error = "app not found" });
            }
            var secret = TokenSecret.Generate(appId);
            tokens.Create(new TokenRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                AppId = appId,
                Name = req.Name,
                Prefix = TokenSecret.Prefix(secret),
                Hash = TokenSecret.Hash(secret),
                Scopes = req.Scopes ?? (TokenScopes.Ingest | TokenScopes.Read),
                ExpiresUtc = req.ExpiresUtc,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            // The full secret is returned exactly once (SPEC §5.2, §9).
            return Results.Ok(new { secret });
        });

        admin.MapGet("/apps/{appId}/tokens", (string appId, TokenStore tokens) =>
            Results.Ok(tokens.ListByApp(appId).Select(t => new
            {
                t.Id, t.Name, t.Prefix, scopes = t.Scopes.ToString(),
                t.ExpiresUtc, t.LastUsedUtc, t.RevokedUtc, t.CreatedUtc,
            })));

        admin.MapPost("/tokens/{id}/revoke", (string id, TokenStore tokens) =>
        {
            tokens.Revoke(id, DateTimeOffset.UtcNow);
            return Results.NoContent();
        });

        // ---- Destinations ----
        admin.MapGet("/destinations", (DestinationStore dests) =>
            Results.Ok(dests.List().Select(PublicDestination)));

        admin.MapGet("/destinations/presets", () => Results.Ok(DestinationPresets.All));

        admin.MapPost("/destinations", (DestinationRequest req, DestinationStore dests, ISecretProtector protector) =>
        {
            var id = Guid.NewGuid().ToString("N");
            dests.Create(new Destination
            {
                Id = id,
                Name = req.Name,
                Url = req.Url,
                Method = req.Method ?? "POST",
                ContentType = req.ContentType ?? "application/json",
                Headers = req.Headers ?? new Dictionary<string, string>(),
                AuthMode = req.AuthMode ?? AuthMode.None,
                AuthSecret = protector.Protect(req.AuthSecret),
                AuthHeaderName = req.AuthHeaderName,
                BodyTemplate = req.BodyTemplate,
                TicketIdPath = req.TicketIdPath,
                TicketUrlPath = req.TicketUrlPath,
                TimeoutSeconds = req.TimeoutSeconds ?? 15,
                MaxAttempts = req.MaxAttempts ?? 6,
                RateLimitPerHour = req.RateLimitPerHour ?? 60,
                IsEnabled = req.IsEnabled ?? true,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return Results.Created($"/api/v1/destinations/{id}", new { id });
        });

        admin.MapPost("/destinations/{id}/enable", (string id, DestinationStore dests) =>
        { dests.SetEnabled(id, true); return Results.NoContent(); });
        admin.MapPost("/destinations/{id}/disable", (string id, DestinationStore dests) =>
        { dests.SetEnabled(id, false); return Results.NoContent(); });

        // Takes the rules that deliver here with it — they cannot outlive their destination.
        admin.MapDelete("/destinations/{id}", (string id, DestinationDeleter deleter) =>
        {
            var result = deleter.Delete(id);
            return result is null ? Results.NotFound(new { error = "destination not found" }) : Results.Ok(result);
        });

        admin.MapPost("/destinations/{id}/test", (string id, ManualTicketService manual) =>
        {
            var result = manual.Test(id);
            return result.Error is null
                ? Results.Ok(new { deliveryId = result.DeliveryId })
                : Results.BadRequest(new { error = result.Error });
        });

        admin.MapPost("/destinations/{id}/preview", (string id, PreviewRequest req,
            DestinationStore dests, EventReader reader, AppStore apps, Microsoft.Extensions.Options.IOptions<ServerOptions> opts) =>
        {
            var dest = dests.Get(id);
            if (dest is null)
            {
                return Results.NotFound();
            }
            var sample = req.SampleEventId is not null && req.AppId is not null
                ? reader.GetById(req.AppId, req.SampleEventId)
                : null;
            var ctx = new WebhookContext
            {
                AppId = req.AppId,
                AppName = req.AppId is not null ? apps.Get(req.AppId)?.Name : null,
                EventId = req.SampleEventId,
                BaseUrl = opts.Value.PublicBaseUrl,
                Event = sample is null ? null : DtoToEvent(sample),
            };
            var rendered = WebhookRenderer.Render(req.Template ?? dest.BodyTemplate, ctx, dest.IsJson);
            return Results.Ok(new { body = rendered.Body, ok = rendered.Ok, error = rendered.Error });
        });

        // ---- Rules ----
        admin.MapGet("/rules", (RuleStore rules) => Results.Ok(rules.List()));

        admin.MapPost("/rules", (RuleRequest req, RuleStore rules) =>
        {
            // Reject an unparseable filter here rather than storing a rule that can never match.
            if (!string.IsNullOrWhiteSpace(req.Filter)
                && !FilterExpression.TryParse(req.Filter, out _, out var filterError))
            {
                return Results.BadRequest(new { error = $"filter does not parse: {filterError}" });
            }

            var id = Guid.NewGuid().ToString("N");
            rules.Create(new Rule
            {
                Id = id,
                Name = req.Name,
                AppId = req.AppId,
                Filter = req.Filter,
                MinimumLevel = req.MinimumLevel ?? LogLevel.Error,
                TriggerType = req.TriggerType ?? TriggerType.EveryMatch,
                ThresholdCount = req.ThresholdCount,
                ThresholdWindowMinutes = req.ThresholdWindowMinutes,
                DedupeKeyTemplate = req.DedupeKeyTemplate ?? "{{event.eventType}}",
                CooldownMinutes = req.CooldownMinutes ?? 60,
                DestinationId = req.DestinationId,
                BodyTemplateOverride = req.BodyTemplateOverride,
                MaxFiresPerHour = req.MaxFiresPerHour ?? 20,
                IsDryRun = req.IsDryRun ?? false,
                IsEnabled = req.IsEnabled ?? true,
                ScopeChangedUtc = DateTimeOffset.UtcNow,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return Results.Created($"/api/v1/rules/{id}", new { id });
        });

        admin.MapPost("/rules/{id}/enable", (string id, RuleStore rules) => { rules.SetEnabled(id, true); return Results.NoContent(); });
        admin.MapPost("/rules/{id}/disable", (string id, RuleStore rules) => { rules.SetEnabled(id, false); return Results.NoContent(); });
        admin.MapPost("/rules/{id}/reset-cooldown", (string id, OccurrenceStore occ) => { occ.ResetForRule(id); return Results.NoContent(); });

        // ---- Deliveries ----
        admin.MapGet("/deliveries", (DeliveryStore deliveries, string? status, string? destinationId) =>
        {
            DeliveryStatus? s = Enum.TryParse<DeliveryStatus>(status, true, out var parsed) ? parsed : null;
            return Results.Ok(deliveries.Query(s, destinationId, null, null));
        });

        admin.MapPost("/deliveries/{id}/retry", (string id, DeliveryStore deliveries) =>
        {
            var d = deliveries.Get(id);
            if (d is null)
            {
                return Results.NotFound();
            }
            deliveries.Update(d with { Status = DeliveryStatus.Pending, NextAttemptUtc = DateTimeOffset.UtcNow });
            return Results.NoContent();
        });

        // ---- Manual ticket (User may create for readable apps) ----
        app.MapPost("/api/v1/tickets", (TicketRequest req, HttpContext http, ManualTicketService manual,
            EventReader reader, AppStore apps) =>
        {
            var ev = req.AppId is not null && req.EventId is not null ? reader.GetById(req.AppId, req.EventId) : null;
            var result = manual.Submit(req.DestinationId, req.AppId,
                req.AppId is not null ? apps.Get(req.AppId)?.Name : null,
                ev is null ? null : DtoToEvent(ev), req.EventId, req.Title, req.Body,
                http.User.Identity?.Name);
            return result.Error is null
                ? Results.Ok(new { deliveryId = result.DeliveryId })
                : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization();
    }

    private static bool SlugOk(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9-]{3,32}$");

    private static object PublicDestination(Destination d) => new
    {
        d.Id, d.Name, d.Url, d.Method, d.ContentType, d.AuthMode, d.IsEnabled,
        d.ConsecutiveFailures, d.CircuitOpenUntilUtc, hasSecret = d.AuthSecret is not null,
    };

    private static LogEvent DtoToEvent(LogEventDto dto) => new()
    {
        Timestamp = dto.Timestamp,
        Level = dto.Level,
        Template = dto.Template,
        Message = dto.Message,
        Exception = dto.Exception,
        EventType = dto.EventType ?? 0,
        TraceId = dto.TraceId,
        Source = dto.Source,
        Properties = new Dictionary<string, object?>(),
    };

    // ---- Request DTOs ----
    public sealed record CreateAppRequest(string Id, string Name, string? Description,
        int? RetentionDays, int? MaxSizeMb, LogLevel? MinimumLevel, List<string>? IndexedProperties);

    public sealed record CreateTokenRequest(string? Name, TokenScopes? Scopes, DateTimeOffset? ExpiresUtc);

    public sealed record DestinationRequest(string Name, string Url, string? Method, string? ContentType,
        Dictionary<string, string>? Headers, AuthMode? AuthMode, string? AuthSecret, string? AuthHeaderName,
        string BodyTemplate, string? TicketIdPath, string? TicketUrlPath, int? TimeoutSeconds,
        int? MaxAttempts, int? RateLimitPerHour, bool? IsEnabled);

    public sealed record PreviewRequest(string? Template, string? AppId, string? SampleEventId);

    public sealed record RuleRequest(string Name, string? AppId, string? Filter, LogLevel? MinimumLevel,
        TriggerType? TriggerType, int? ThresholdCount, int? ThresholdWindowMinutes, string? DedupeKeyTemplate,
        int? CooldownMinutes, string DestinationId, string? BodyTemplateOverride, int? MaxFiresPerHour,
        bool? IsDryRun, bool? IsEnabled);

    public sealed record TicketRequest(string DestinationId, string? AppId, string? EventId, string? Title, string? Body);
}
