using System.Collections.Concurrent;
using System.Text.Json;
using Logrr.Core;
using Logrr.Core.Filters;
using Logrr.Storage;

namespace Logrr.Notify;

/// <summary>
/// Evaluates rules against a committed batch (SPEC §10.3, §10.4, §10.7), using the same
/// compiled predicate as the filter language so ingest-to-webhook latency is sub-second
/// with no polling loop. Runs post-commit, off the writer's disk path.
/// </summary>
public sealed class RuleEngine(
    RuleStore rules,
    DestinationStore destinations,
    OccurrenceStore occurrences,
    DeliveryStore deliveries,
    NotifyOptions options,
    Func<string, string?> appNameFor,
    Func<DateTimeOffset> clock,
    Action<string>? onAutoDisable = null)
{
    private readonly ConcurrentDictionary<string, Func<LogEvent, bool>> _predicates = new();

    public void Evaluate(CommitBatch commit)
    {
        var now = clock();
        var maxAge = TimeSpan.FromMinutes(options.RuleEvaluationMaxAgeMinutes);
        var applicable = rules.EnabledForApp(commit.AppId);
        if (applicable.Count == 0)
        {
            return;
        }

        var appName = appNameFor(commit.AppId);

        foreach (var rule in applicable)
        {
            // Backfill protection: only recent events can fire a rule (SPEC §10.7).
            var matches = commit.Rows
                .Where(r => now - r.Event.Timestamp <= maxAge)
                .Where(r => r.Event.Level >= rule.MinimumLevel)
                .Where(r => Predicate(rule) is not { } p || p(r.Event))
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            // Per-rule hourly fire cap → auto-disable (fail loud and stopped, SPEC §10.7).
            if (deliveries.CountForRuleSince(rule.Id, now.AddHours(-1)) >= rule.MaxFiresPerHour)
            {
                rules.AutoDisable(rule.Id, $"exceeded {rule.MaxFiresPerHour} fires/hour");
                onAutoDisable?.Invoke(rule.Id);
                continue;
            }

            // Storm collapse: a firehose becomes one summary delivery (SPEC §10.7).
            if (matches.Count > NotifyOptions.StormRatePerSecond)
            {
                FireStorm(rule, commit, matches, appName, now);
                continue;
            }

            foreach (var (rowid, ev) in matches)
            {
                EvaluateMatch(rule, commit, rowid, ev, appName, now);
            }
        }
    }

    private void EvaluateMatch(Rule rule, CommitBatch commit, long rowid, LogEvent ev, string? appName, DateTimeOffset now)
    {
        var eventId = EventId.Format(commit.Day, rowid);
        var dedupeKey = RenderKey(rule, ev, eventId, commit.AppId, appName);

        var existing = occurrences.Get(rule.Id, dedupeKey);
        var windowMinutes = rule.ThresholdWindowMinutes ?? rule.CooldownMinutes;
        var windowStart = existing?.WindowStartUtc ?? now;
        var count = existing?.Count ?? 0;

        // Slide the window if it has expired.
        if (existing is not null && now - windowStart > TimeSpan.FromMinutes(windowMinutes))
        {
            windowStart = now;
            count = 0;
        }
        count++;

        var occurrence = new Occurrence
        {
            RuleId = rule.Id,
            DedupeKey = dedupeKey,
            WindowStartUtc = windowStart,
            Count = count,
            FirstSeenUtc = existing?.FirstSeenUtc ?? now,
            LastSeenUtc = now,
            SampleEvent = SampleJson(ev),
            LastFiredUtc = existing?.LastFiredUtc,
            TicketUrl = existing?.TicketUrl,
        };

        var shouldFire = ShouldFire(rule, occurrence, now);
        if (shouldFire)
        {
            occurrence = occurrence with { LastFiredUtc = now };
            if (!rule.IsDryRun)
            {
                EnqueueDelivery(rule, commit.AppId, appName, ev, eventId, occurrence, now);
            }
            rules.SetLastFired(rule.Id, now);
        }

        occurrences.Upsert(occurrence);
    }

    private bool ShouldFire(Rule rule, Occurrence occurrence, DateTimeOffset now)
    {
        // Cooldown suppresses re-firing the same key; occurrences keep accumulating.
        if (occurrence.LastFiredUtc is { } last && now - last < TimeSpan.FromMinutes(rule.CooldownMinutes))
        {
            return false;
        }

        return rule.TriggerType switch
        {
            TriggerType.EveryMatch => true,
            TriggerType.Threshold => occurrence.Count >= (rule.ThresholdCount ?? int.MaxValue),
            _ => false,
        };
    }

    private void FireStorm(Rule rule, CommitBatch commit, List<(long Rowid, LogEvent Event)> matches, string? appName, DateTimeOffset now)
    {
        var (rowid, ev) = matches[0];
        var eventId = EventId.Format(commit.Day, rowid);
        var occurrence = new Occurrence
        {
            RuleId = rule.Id,
            DedupeKey = "storm",
            WindowStartUtc = now,
            Count = matches.Count,
            FirstSeenUtc = now,
            LastSeenUtc = now,
            SampleEvent = SampleJson(ev),
            LastFiredUtc = now,
        };
        if (!rule.IsDryRun)
        {
            EnqueueDelivery(rule, commit.AppId, appName, ev, eventId, occurrence, now);
        }
        occurrences.Upsert(occurrence);
        rules.SetLastFired(rule.Id, now);
    }

    private void EnqueueDelivery(Rule rule, string appId, string? appName, LogEvent ev,
        string eventId, Occurrence occurrence, DateTimeOffset now)
    {
        var destination = destinations.Get(rule.DestinationId);
        if (destination is null || !destination.IsEnabled)
        {
            return;
        }

        // Per-destination hourly rate limit, shared across rules (SPEC §10.7).
        if (deliveries.CountSince(now.AddHours(-1), destination.Id) >= destination.RateLimitPerHour)
        {
            return;
        }

        var template = rule.BodyTemplateOverride ?? destination.BodyTemplate;
        var ctx = new WebhookContext
        {
            Event = ev,
            EventId = eventId,
            AppId = appId,
            AppName = appName,
            Rule = rule,
            Occurrence = occurrence,
            BaseUrl = options.PublicBaseUrl,
        };

        var rendered = WebhookRenderer.Render(template, ctx, destination.IsJson);

        deliveries.Enqueue(new Delivery
        {
            Id = Guid.NewGuid().ToString("N"),
            DestinationId = destination.Id,
            RuleId = rule.Id,
            AppId = appId,
            Source = DeliverySource.Rule,
            CreatedUtc = now,
            Attempt = 0,
            NextAttemptUtc = now,
            Status = rendered.Ok ? DeliveryStatus.Pending : DeliveryStatus.Failed,
            RequestBody = rendered.Body,
            Error = rendered.Ok ? null : rendered.Error,
        });
    }

    private string RenderKey(Rule rule, LogEvent ev, string eventId, string appId, string? appName)
    {
        var ctx = new WebhookContext
        {
            Event = ev,
            EventId = eventId,
            AppId = appId,
            AppName = appName,
            Rule = rule,
            BaseUrl = options.PublicBaseUrl,
        };
        return WebhookTemplate.Render(rule.DedupeKeyTemplate, path => ResolveKey(path, ctx), jsonMode: false);
    }

    private static string? ResolveKey(string path, WebhookContext ctx)
    {
        // Reuse the renderer's resolution by rendering a single-token template is overkill;
        // handle the small set of paths used in dedupe keys directly.
        var e = ctx.Event!;
        if (path.StartsWith("event.properties.", StringComparison.Ordinal))
        {
            var name = path["event.properties.".Length..];
            return e.Properties.TryGetValue(name, out var v) ? PropertyValue.ToInvariantString(v) : "";
        }
        return path switch
        {
            "event.eventType" => e.EventType.ToString(),
            "event.level" => LevelMap.ToName(e.Level),
            "event.source" => e.Source ?? "",
            "event.message" => e.Message,
            "app.id" => ctx.AppId ?? "",
            _ => "",
        };
    }

    private Func<LogEvent, bool>? Predicate(Rule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Filter))
        {
            return null;
        }
        return _predicates.GetOrAdd(rule.Filter, f => FilterExpression.Parse(f).Compile());
    }

    private static string SampleJson(LogEvent ev) => JsonSerializer.Serialize(new
    {
        timestamp = ev.Timestamp,
        level = LevelMap.ToName(ev.Level),
        message = ev.Message,
        exception = ev.Exception,
    });
}
