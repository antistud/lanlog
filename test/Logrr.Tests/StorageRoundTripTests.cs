using Logrr.Contracts;
using Logrr.Core;
using Logrr.Storage;
using Xunit;

namespace Logrr.Tests;

public class StorageRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-test-" + Guid.NewGuid().ToString("N"));

    private (PartitionManager Pm, EventReader Reader) NewStore()
    {
        var paths = new StoragePaths(_root);
        paths.EnsureRootDirectories();
        var pm = new PartitionManager(paths);
        return (pm, new EventReader(pm));
    }

    private static LogEvent Ev(DateTimeOffset ts, LogLevel level, string message,
        string? exception = null, params (string, object?)[] props)
    {
        var bag = props.ToDictionary(p => p.Item1, p => p.Item2);
        return new LogEvent
        {
            Timestamp = ts,
            Level = level,
            Message = message,
            Exception = exception,
            EventType = EventTypeHash.Compute(null, message),
            Properties = bag,
        };
    }

    [Fact]
    public void Writes_and_reads_back_events()
    {
        var (pm, reader) = NewStore();
        var day = new DateOnly(2026, 7, 23);
        var t0 = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        var events = new List<LogEvent>
        {
            Ev(t0, LogLevel.Information, "user login ok", props: ("UserId", 1L)),
            Ev(t0.AddSeconds(1), LogLevel.Error, "payment failed", "System.TimeoutException", ("UserId", 1042L)),
            Ev(t0.AddSeconds(2), LogLevel.Warning, "cache miss timeout"),
        };
        pm.GetWriter("billing", day, []).InsertBatch(events);

        var page = reader.Query(new EventQuery { AppId = "billing", Limit = 100 });
        Assert.Equal(3, page.Events.Count);
        // Newest first.
        Assert.Equal("cache miss timeout", page.Events[0].Message);
        Assert.Equal(LogLevel.Error, page.Events[1].Level);

        pm.Dispose();
    }

    [Fact]
    public void Filters_and_full_text_search_apply()
    {
        var (pm, reader) = NewStore();
        var day = new DateOnly(2026, 7, 23);
        var t0 = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        pm.GetWriter("billing", day, ["UserId"]).InsertBatch(
        [
            Ev(t0, LogLevel.Information, "user login ok", props: ("UserId", 1L)),
            Ev(t0.AddSeconds(1), LogLevel.Error, "payment timeout", "boom", ("UserId", 1042L)),
            Ev(t0.AddSeconds(2), LogLevel.Error, "payment ok", props: ("UserId", 7L)),
        ]);

        var filtered = reader.Query(new EventQuery
        {
            AppId = "billing",
            Filter = "Level >= Error and UserId = 1042",
        });
        Assert.Single(filtered.Events);
        Assert.Equal("payment timeout", filtered.Events[0].Message);

        var searched = reader.Query(new EventQuery { AppId = "billing", Text = "timeout" });
        Assert.Single(searched.Events);
        Assert.Equal("payment timeout", searched.Events[0].Message);

        pm.Dispose();
    }

    [Fact]
    public void Cursor_paging_is_stable_and_complete()
    {
        var (pm, reader) = NewStore();
        var day = new DateOnly(2026, 7, 23);
        var t0 = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var batch = Enumerable.Range(0, 25)
            .Select(i => Ev(t0.AddSeconds(i), LogLevel.Information, $"event {i}"))
            .ToList();
        pm.GetWriter("billing", day, []).InsertBatch(batch);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = reader.Query(new EventQuery { AppId = "billing", Limit = 10, Cursor = cursor });
            seen.AddRange(page.Events.Select(e => e.Message));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count()); // no dupes, no gaps
        pm.Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
