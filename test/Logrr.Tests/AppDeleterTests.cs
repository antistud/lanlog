using Logrr.Contracts;
using Logrr.Core;
using Logrr.Notify;
using Logrr.Server.Admin;
using Logrr.Server.Auth;
using Logrr.Server.WindowsEvents;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Tests;

/// <summary>
/// Deleting an app removes everything scoped to it and nothing else — the neighbouring app and
/// the all-apps rule in each case are the point of the test.
/// </summary>
public class AppDeleterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-appdel-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly StoragePaths _paths;
    private readonly ControlDatabase _db;
    private readonly PartitionManager _partitions;
    private readonly AppStore _apps;
    private readonly TokenStore _tokens;
    private readonly SavedSearchStore _searches;
    private readonly AckStore _acks;
    private readonly RuleStore _rules;
    private readonly OccurrenceStore _occurrences;
    private readonly TicketLinkStore _ticketLinks;
    private readonly DeliveryStore _deliveries;
    private readonly DestinationStore _destinations;
    private readonly WindowsEventSettings _winlog;
    private readonly AppDeleter _deleter;

    public AppDeleterTests()
    {
        _paths = new StoragePaths(_root);
        _paths.EnsureRootDirectories();
        _db = new ControlDatabase(_paths);
        _db.Initialize();
        _partitions = new PartitionManager(_paths);
        _apps = new AppStore(_db);
        _tokens = new TokenStore(_db);
        _searches = new SavedSearchStore(_db);
        _acks = new AckStore(_db);
        _rules = new RuleStore(_db);
        _occurrences = new OccurrenceStore(_db);
        _ticketLinks = new TicketLinkStore(_db);
        _deliveries = new DeliveryStore(_db);
        _destinations = new DestinationStore(_db);
        _winlog = new WindowsEventSettings(
            new WinlogConfigStore(_db), new WinlogCursorStore(_db),
            new ConfigurationBuilder().Build(), () => _now,
            NullLogger<WindowsEventSettings>.Instance);
        _deleter = new AppDeleter(
            _apps, _tokens, _searches, _acks, _rules, _occurrences, _ticketLinks, _deliveries,
            _winlog, _partitions, _paths,
            new TokenAuthenticator(_tokens, _apps, new MemoryCache(new MemoryCacheOptions())),
            NullLogger<AppDeleter>.Instance);

        // rules.destination_id is a foreign key, so the rules below need a real destination.
        _destinations.Create(new Destination
        {
            Id = "dest", Name = "ops", Url = "https://hooks.example/ops", BodyTemplate = "{}",
            CreatedUtc = _now,
        });
    }

    [Fact]
    public void Delete_removes_everything_scoped_to_the_app()
    {
        Seed("billing");
        Seed("web");

        var result = _deleter.Delete("billing");

        Assert.NotNull(result);
        Assert.Equal(2, result.Partitions);
        Assert.Equal(1, result.Tokens);
        Assert.Equal(1, result.Rules);
        Assert.Equal(1, result.SavedSearches);
        Assert.Equal(1, result.WindowsSources);

        Assert.Null(_apps.Get("billing"));
        Assert.Empty(_tokens.ListByApp("billing"));
        Assert.Empty(_searches.ListByApp("billing"));
        Assert.Empty(_acks.ListByApp("billing"));
        Assert.Empty(_ticketLinks.ListByApp("billing"));
        Assert.Empty(_rules.ListByApp("billing"));
        Assert.DoesNotContain(_deliveries.Query(null, null, null, null), d => d.AppId == "billing");
        Assert.DoesNotContain(_winlog.Sources(), s => s.AppId == "billing");
        Assert.Null(_occurrences.Get("rule-billing", "key"));

        // Events and the folder holding them.
        Assert.Empty(_partitions.ExistingDaysDescending("billing"));
        Assert.False(Directory.Exists(_paths.AppDir("billing")));
    }

    [Fact]
    public void Delete_leaves_other_apps_and_all_apps_rules_alone()
    {
        Seed("billing");
        Seed("web");
        _rules.Create(NewRule("rule-global", appId: null));

        _deleter.Delete("billing");

        Assert.NotNull(_apps.Get("web"));
        Assert.Single(_tokens.ListByApp("web"));
        Assert.Single(_searches.ListByApp("web"));
        Assert.Single(_acks.ListByApp("web"));
        Assert.Single(_ticketLinks.ListByApp("web"));
        Assert.Single(_rules.ListByApp("web"));
        Assert.Single(_winlog.Sources());
        Assert.NotNull(_occurrences.Get("rule-web", "key"));
        Assert.Equal(2, _partitions.ExistingDaysDescending("web").Count);

        // An all-apps rule outlives the app it happened to be firing on.
        Assert.NotNull(_rules.Get("rule-global"));
    }

    [Fact]
    public void Delete_of_an_unknown_app_reports_nothing_deleted()
    {
        Assert.Null(_deleter.Delete("no-such-app"));
    }

    // ---- Fixture -----------------------------------------------------------------------

    /// <summary>An app with one of everything that hangs off an app.</summary>
    private void Seed(string appId)
    {
        _apps.Create(new AppRecord
        {
            Id = appId, Name = appId, RetentionDays = 14, MaxSizeMb = 2048,
            MinimumLevel = LogLevel.Verbose, IsEnabled = true, CreatedUtc = _now,
        });

        _tokens.Create(new TokenRecord
        {
            Id = $"tok-{appId}", AppId = appId, Prefix = $"lgr_{appId}", Hash = [1, 2, 3],
            Scopes = TokenScopes.Ingest, CreatedUtc = _now,
        });

        _searches.Create(new SavedSearch($"search-{appId}", appId, "Errors", "level=Error", "admin", _now));

        _acks.Acknowledge(new Ack(appId, Ack.AllScope, 1, null, "admin", _now));

        _rules.Create(NewRule($"rule-{appId}", appId));
        _occurrences.Upsert(new Occurrence
        {
            RuleId = $"rule-{appId}", DedupeKey = "key", WindowStartUtc = _now, Count = 1,
            FirstSeenUtc = _now, LastSeenUtc = _now,
        });

        _ticketLinks.Create(new TicketLink
        {
            Id = $"link-{appId}", AppId = appId, EventType = 42, TicketUrl = "https://tickets/1",
            CreatedUtc = _now,
        });

        _deliveries.Enqueue(new Delivery
        {
            Id = $"del-{appId}", DestinationId = "dest", AppId = appId, RuleId = $"rule-{appId}",
            Source = DeliverySource.Rule, Status = DeliveryStatus.Delivered, CreatedUtc = _now,
        });

        _winlog.SaveSource(new WinlogSource
        {
            Id = $"src-{appId}", Machine = appId, AppId = appId, IsEnabled = true, CreatedUtc = _now,
        }, isNew: true);

        // Two days of events, so the delete has more than one partition to drop.
        foreach (var day in new[] { new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 20) })
        {
            _partitions.GetWriter(appId, day, []).InsertBatch([new LogEvent
            {
                Timestamp = new DateTimeOffset(day, new TimeOnly(10, 0), TimeSpan.Zero),
                Level = LogLevel.Error,
                Message = "boom",
                EventType = EventTypeHash.Compute(null, "boom"),
                Properties = new Dictionary<string, object?>(),
            }]);
        }
    }

    private Rule NewRule(string id, string? appId) => new()
    {
        Id = id, Name = id, AppId = appId, MinimumLevel = LogLevel.Error,
        TriggerType = TriggerType.EveryMatch, DestinationId = "dest", CreatedUtc = _now,
    };

    public void Dispose()
    {
        _partitions.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
