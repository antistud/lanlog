using Logrr.Contracts;
using Logrr.Notify;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// Destinations are deleted with the rules that point at them (SPEC §10.1) — a rule holds a
/// NOT NULL foreign key to its destination, so a plain delete is rejected by the database.
/// </summary>
public class DestinationDeleterTests : IDisposable
{
    private readonly NotifyTestHarness _h = new();
    private readonly DestinationDeleter _deleter;
    private readonly DateTimeOffset _now = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    public DestinationDeleterTests()
    {
        _deleter = new DestinationDeleter(_h.Destinations, _h.Rules, _h.Occurrences);
        _h.Destinations.Create(Dest("keep"));
        _h.Destinations.Create(Dest("drop"));
        _h.Rules.Create(Rule("rule-a", "drop"));
        _h.Rules.Create(Rule("rule-b", "drop"));
        _h.Rules.Create(Rule("rule-c", "keep"));
        _h.Occurrences.Upsert(new Occurrence
        {
            RuleId = "rule-a", DedupeKey = "key", WindowStartUtc = _now, Count = 3,
            FirstSeenUtc = _now, LastSeenUtc = _now,
        });
    }

    [Fact]
    public void Delete_takes_the_rules_that_deliver_there_with_it()
    {
        Assert.Equal(2, _deleter.RulesUsing("drop").Count);

        var result = _deleter.Delete("drop");

        Assert.NotNull(result);
        Assert.Equal(2, result.RulesDeleted);
        Assert.Null(_h.Destinations.Get("drop"));
        Assert.Null(_h.Rules.Get("rule-a"));
        Assert.Null(_h.Rules.Get("rule-b"));
        Assert.Null(_h.Occurrences.Get("rule-a", "key"));

        // The neighbouring destination and its rule are untouched.
        Assert.NotNull(_h.Destinations.Get("keep"));
        Assert.NotNull(_h.Rules.Get("rule-c"));
    }

    [Fact]
    public void Delete_of_an_unused_destination_leaves_the_rules_alone()
    {
        _h.Destinations.Create(Dest("unused"));

        var result = _deleter.Delete("unused");

        Assert.NotNull(result);
        Assert.Equal(0, result.RulesDeleted);
        Assert.Equal(3, _h.Rules.List().Count);
    }

    [Fact]
    public void Delete_of_an_unknown_destination_reports_nothing_deleted()
    {
        Assert.Null(_deleter.Delete("no-such-destination"));
    }

    [Fact]
    public void Disabling_survives_a_later_edit()
    {
        _h.Destinations.SetEnabled("keep", false);
        Assert.False(_h.Destinations.Get("keep")!.IsEnabled);

        // The editor writes the whole record back; the flag it carries is what sticks.
        var edited = _h.Destinations.Get("keep")! with { Name = "renamed" };
        _h.Destinations.Update(edited);

        var after = _h.Destinations.Get("keep")!;
        Assert.Equal("renamed", after.Name);
        Assert.False(after.IsEnabled);
    }

    private Destination Dest(string id) => new()
    {
        Id = id, Name = id, Url = $"https://hooks.example/{id}", BodyTemplate = "{}", CreatedUtc = _now,
    };

    private Rule Rule(string id, string destinationId) => new()
    {
        Id = id, Name = id, MinimumLevel = LogLevel.Error, TriggerType = TriggerType.EveryMatch,
        DestinationId = destinationId, CreatedUtc = _now,
    };

    public void Dispose() => _h.Dispose();
}
