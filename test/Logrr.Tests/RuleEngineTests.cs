using Logrr.Contracts;
using Logrr.Core;
using Logrr.Notify;
using Logrr.Storage;
using Xunit;

namespace Logrr.Tests;

public class RuleEngineTests : IDisposable
{
    private readonly NotifyTestHarness _h = new();
    private DateTimeOffset _now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private RuleEngine Engine() => new(
        _h.Rules, _h.Destinations, _h.Occurrences, _h.Deliveries,
        new NotifyOptions(), _ => "Billing", () => _now);

    private void AddDestination(string id = "d1") => _h.Destinations.Create(new Destination
    {
        Id = id, Name = "Generic", Url = "https://example/hook",
        BodyTemplate = """{ "message": "{{event.message}}", "count": {{occurrence.count}} }""",
        CreatedUtc = _now,
    });

    private string AddRule(TriggerType trigger = TriggerType.EveryMatch, int? thresholdCount = null,
        int cooldown = 60, bool dryRun = false, string dest = "d1", string? filter = null,
        int maxFires = 20, LogLevel minimumLevel = LogLevel.Error,
        DateTimeOffset? scopeChanged = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _h.Rules.Create(new Rule
        {
            Id = id, Name = "errors", AppId = "billing", Filter = filter,
            MinimumLevel = minimumLevel, TriggerType = trigger,
            ThresholdCount = thresholdCount, ThresholdWindowMinutes = 10,
            CooldownMinutes = cooldown, DestinationId = dest, IsDryRun = dryRun,
            MaxFiresPerHour = maxFires, ScopeChangedUtc = scopeChanged,
            IsEnabled = true, CreatedUtc = _now,
        });
        return id;
    }

    /// <summary>A batch of events that each dedupe separately — one delivery per event.</summary>
    private CommitBatch DistinctBatch(int count, DateTimeOffset? ts = null)
    {
        var t = ts ?? _now;
        var rows = Enumerable.Range(0, count)
            .Select(i =>
            {
                var message = $"failure {i}";
                return ((long)(i + 1), new LogEvent
                {
                    Timestamp = t,
                    Level = LogLevel.Error,
                    Template = message,
                    Message = message,
                    EventType = EventTypeHash.Compute(message, message),
                    Properties = new Dictionary<string, object?>(),
                });
            })
            .ToList();
        return new CommitBatch("billing", StoragePaths.DayOf(t), rows);
    }

    private CommitBatch Batch(int count, DateTimeOffset? ts = null, string message = "payment failed")
    {
        var t = ts ?? _now;
        var rows = Enumerable.Range(0, count)
            .Select(i => ((long)(i + 1), new LogEvent
            {
                Timestamp = t,
                Level = LogLevel.Error,
                Template = message, // same template → same event_type → same dedupe key
                Message = message,
                EventType = EventTypeHash.Compute(message, message),
                Properties = new Dictionary<string, object?>(),
            }))
            .ToList();
        return new CommitBatch("billing", StoragePaths.DayOf(t), rows);
    }

    [Fact]
    public void Editing_a_rule_does_not_alert_on_events_that_predate_the_edit()
    {
        AddDestination();
        // Widened from Error to Debug five minutes ago — still inside the 15-minute backfill
        // window, so without the floor the whole backlog would fire.
        var edited = _now.AddMinutes(-5);
        AddRule(minimumLevel: LogLevel.Debug, scopeChanged: edited, maxFires: 100);

        Engine().Evaluate(DistinctBatch(37, ts: _now.AddMinutes(-10)));
        Assert.Empty(_h.Deliveries.Query(null, null, null, null));

        // Events after the edit still alert normally.
        Engine().Evaluate(DistinctBatch(2, ts: _now.AddMinutes(-1)));
        Assert.Equal(2, _h.Deliveries.Query(null, null, null, null).Count);
    }

    [Fact]
    public void Hourly_fire_cap_holds_within_a_single_batch()
    {
        AddDestination();
        var id = AddRule(maxFires: 20);

        // 37 distinct dedupe keys arriving in one commit: the cap must stop it at 20, not
        // let the whole batch through because the count was only checked up front.
        Engine().Evaluate(DistinctBatch(37));

        Assert.Equal(20, _h.Deliveries.Query(null, null, null, null).Count);
        Assert.False(_h.Rules.Get(id)!.IsEnabled);
    }

    [Fact]
    public void Unparseable_filter_disables_only_its_own_rule()
    {
        AddDestination();
        // "Application % has shutdown." is a message, not an expression — the lexer rejects '%'.
        var bad = AddRule(filter: "Application % has shutdown.");
        var good = AddRule();

        Engine().Evaluate(Batch(1));

        // The bad rule must not take the good one down with it.
        Assert.Single(_h.Deliveries.Query(null, null, null, null));
        Assert.NotNull(_h.Rules.Get(good)!.LastFiredUtc);

        var disabled = _h.Rules.Get(bad)!;
        Assert.False(disabled.IsEnabled);
        Assert.Contains("filter does not parse", disabled.AutoDisabledReason);
    }

    [Fact]
    public void Dedupe_collapses_identical_events_to_one_delivery()
    {
        AddDestination();
        AddRule();
        Engine().Evaluate(Batch(1000)); // a thousand identical NREs

        Assert.Single(_h.Deliveries.Query(null, null, null, null));
    }

    [Fact]
    public void Threshold_fires_only_after_count_reached()
    {
        AddDestination();
        AddRule(TriggerType.Threshold, thresholdCount: 5);

        Engine().Evaluate(Batch(3));
        Assert.Empty(_h.Deliveries.Query(null, null, null, null));

        Engine().Evaluate(Batch(2)); // now 5 within the window
        Assert.Single(_h.Deliveries.Query(null, null, null, null));
    }

    [Fact]
    public void Backfill_protection_skips_old_events()
    {
        AddDestination();
        AddRule();
        // 20 minutes old — beyond the 15-minute evaluation window.
        Engine().Evaluate(Batch(5, ts: _now.AddMinutes(-20)));
        Assert.Empty(_h.Deliveries.Query(null, null, null, null));
    }

    [Fact]
    public void Dry_run_records_occurrence_but_sends_nothing()
    {
        AddDestination();
        var ruleId = AddRule(dryRun: true);
        Engine().Evaluate(Batch(3));

        Assert.Empty(_h.Deliveries.Query(null, null, null, null));
        Assert.NotNull(_h.Occurrences.Get(ruleId, EventTypeHash.Compute("payment failed", "payment failed").ToString()));
    }

    [Fact]
    public void Cooldown_suppresses_refire_across_batches()
    {
        AddDestination();
        AddRule(cooldown: 60);

        Engine().Evaluate(Batch(1));
        _now = _now.AddMinutes(5);         // still within cooldown
        Engine().Evaluate(Batch(1));
        Assert.Single(_h.Deliveries.Query(null, null, null, null));

        _now = _now.AddMinutes(60);        // cooldown elapsed
        Engine().Evaluate(Batch(1));
        Assert.Equal(2, _h.Deliveries.Query(null, null, null, null).Count);
    }

    public void Dispose() => _h.Dispose();
}
