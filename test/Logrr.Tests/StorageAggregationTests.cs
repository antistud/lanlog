using Logrr.Contracts;
using Logrr.Core;
using Logrr.Storage;
using Xunit;

namespace Logrr.Tests;

public class StorageAggregationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-agg-" + Guid.NewGuid().ToString("N"));
    private readonly PartitionManager _pm;
    private readonly EventReader _reader;
    private readonly DateTimeOffset _t0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public StorageAggregationTests()
    {
        var paths = new StoragePaths(_root);
        paths.EnsureRootDirectories();
        _pm = new PartitionManager(paths);
        _reader = new EventReader(_pm);
    }

    private static LogEvent Ev(DateTimeOffset ts, LogLevel level, string message, string? source = null)
        => new()
        {
            Timestamp = ts, Level = level, Message = message,
            EventType = EventTypeHash.Compute(null, message), Source = source,
            Properties = new Dictionary<string, object?>(),
        };

    private void Seed()
    {
        var day = new DateOnly(2026, 7, 23);
        _pm.GetWriter("billing", day, []).InsertBatch(
        [
            Ev(_t0.AddMinutes(1), LogLevel.Information, "a", "Api"),
            Ev(_t0.AddMinutes(2), LogLevel.Information, "b", "Api"),
            Ev(_t0.AddMinutes(3), LogLevel.Error, "c", "Worker"),
            Ev(_t0.AddMinutes(50), LogLevel.Warning, "d", "Api"),
        ]);
    }

    [Fact]
    public void Histogram_buckets_counts_by_level_over_the_range()
    {
        Seed();
        var hist = _reader.Histogram(new EventQuery
        {
            AppId = "billing", From = _t0, To = _t0.AddHours(1),
        }, buckets: 60); // 1-minute buckets

        Assert.Equal(4, hist.Total);
        Assert.Equal(60, hist.Buckets.Count);

        // Two Information events landed in the minute-1 and minute-2 buckets.
        Assert.Equal(1, hist.Buckets[1].Counts[LogLevel.Information]);
        Assert.Equal(1, hist.Buckets[2].Counts[LogLevel.Information]);
        Assert.Equal(1, hist.Buckets[3].Counts[LogLevel.Error]);
        Assert.Equal(1, hist.Buckets[50].Counts[LogLevel.Warning]);
        Assert.Equal(1, hist.Max); // busiest bucket holds a single event
    }

    [Fact]
    public void Histogram_respects_the_same_filters_as_the_table()
    {
        Seed();
        var hist = _reader.Histogram(new EventQuery
        {
            AppId = "billing", From = _t0, To = _t0.AddHours(1), MinLevel = LogLevel.Warning,
        }, buckets: 60);

        Assert.Equal(2, hist.Total); // only the Error and the Warning
    }

    [Fact]
    public void Facets_count_levels_and_top_sources()
    {
        Seed();
        var facets = _reader.Facets(new EventQuery { AppId = "billing" });

        var levels = facets.Levels.ToDictionary(l => l.Level, l => l.Count);
        Assert.Equal(2, levels[LogLevel.Information]);
        Assert.Equal(1, levels[LogLevel.Error]);
        Assert.Equal(1, levels[LogLevel.Warning]);

        Assert.Equal("Api", facets.Sources[0].Value); // most frequent source first
        Assert.Equal(3, facets.Sources[0].Count);
        Assert.Contains(facets.Sources, s => s.Value == "Worker" && s.Count == 1);
    }

    public void Dispose()
    {
        _pm.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
