using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core;
using Logrr.Server.Exploring;
using Logrr.Storage;
using Xunit;

namespace Logrr.Tests;

public class TraceReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-test-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private (PartitionManager Pm, TraceReader Reader) NewStore()
    {
        var paths = new StoragePaths(_root);
        paths.EnsureRootDirectories();
        var pm = new PartitionManager(paths);
        return (pm, new TraceReader(pm));
    }

    private static LogEvent Ev(DateTimeOffset ts, string message, string? traceId, string? spanId = null) => new()
    {
        Timestamp = ts,
        Level = LogLevel.Information,
        Message = message,
        EventType = EventTypeHash.Compute(null, message),
        TraceId = traceId,
        SpanId = spanId,
    };

    [Fact]
    public void Assembles_a_trace_across_apps_oldest_first()
    {
        var (pm, traces) = NewStore();
        var day = new DateOnly(2026, 7, 23);

        pm.GetWriter("web", day, []).InsertBatch(
        [
            Ev(T0, "request start", "abc"),
            Ev(T0.AddSeconds(1), "unrelated", "zzz"),
            Ev(T0.AddSeconds(3), "request end", "abc"),
        ]);
        pm.GetWriter("worker", day, []).InsertBatch(
        [
            Ev(T0.AddSeconds(2), "job ran", "abc"),
        ]);

        var trace = traces.Read("abc", ["web", "worker"], near: T0);

        Assert.Equal(["request start", "job ran", "request end"],
            trace.Events.Select(e => e.Event.Message));
        Assert.Equal(["web", "worker", "web"], trace.Events.Select(e => e.AppId));
        Assert.False(trace.Truncated);

        pm.Dispose();
    }

    [Fact]
    public void Only_scans_the_days_around_the_anchor()
    {
        var (pm, traces) = NewStore();

        pm.GetWriter("web", new DateOnly(2026, 7, 23), []).InsertBatch([Ev(T0, "today", "abc")]);
        pm.GetWriter("web", new DateOnly(2026, 7, 12), []).InsertBatch(
            [Ev(T0.AddDays(-11), "long ago", "abc")]);

        var anchored = traces.Read("abc", ["web"], near: T0);
        Assert.Equal(["today"], anchored.Events.Select(e => e.Event.Message));
        Assert.Equal(1, anchored.PartitionsScanned);

        // With no anchor there is no window, so the bound is the newest few partitions
        // instead — a trace id on its own is still chased back through them.
        var unanchored = traces.Read("abc", ["web"]);
        Assert.Equal(["long ago", "today"], unanchored.Events.Select(e => e.Event.Message));
        Assert.Equal(2, unanchored.PartitionsScanned);

        pm.Dispose();
    }

    [Fact]
    public void Truncation_keeps_the_start_of_the_trace()
    {
        var (pm, traces) = NewStore();
        var day = new DateOnly(2026, 7, 23);
        pm.GetWriter("web", day, []).InsertBatch(
            Enumerable.Range(0, 5).Select(i => Ev(T0.AddSeconds(i), $"step {i}", "abc")).ToList());

        var trace = traces.Read("abc", ["web"], near: T0, limit: 3);

        Assert.True(trace.Truncated);
        Assert.Equal(["step 0", "step 1", "step 2"], trace.Events.Select(e => e.Event.Message));

        pm.Dispose();
    }

    [Fact]
    public void Unknown_or_empty_trace_id_reads_nothing()
    {
        var (pm, traces) = NewStore();
        pm.GetWriter("web", new DateOnly(2026, 7, 23), []).InsertBatch([Ev(T0, "hello", "abc")]);

        Assert.Empty(traces.Read("nope", ["web"], near: T0).Events);
        Assert.Empty(traces.Read("", ["web"], near: T0).Events);

        pm.Dispose();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

public class TraceTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private static TraceEntryDto Entry(
        string id, DateTimeOffset ts, string message,
        string? spanId = null, string? parentSpanId = null, DateTimeOffset? spanStart = null,
        string appId = "web")
    {
        var props = new Dictionary<string, object?>();
        if (parentSpanId is not null) { props[SpanFields.ParentSpanId] = parentSpanId; }
        if (spanStart is { } s) { props[SpanFields.SpanStart] = s.ToString("O"); }

        return new TraceEntryDto
        {
            AppId = appId,
            Event = new LogEventDto
            {
                Id = id,
                Timestamp = ts,
                Level = LogLevel.Information,
                Message = message,
                SpanId = spanId,
                Properties = props.Count == 0 ? null : JsonSerializer.SerializeToElement(props),
            },
        };
    }

    private static TraceResponse Trace(params TraceEntryDto[] entries) =>
        new() { TraceId = "abc", Events = entries };

    [Fact]
    public void Nests_spans_and_hangs_log_lines_under_the_span_they_were_written_in()
    {
        var timeline = TraceTimeline.Build(Trace(
            Entry("1", T0.AddSeconds(1), "GET /orders", spanId: "a", spanStart: T0),
            Entry("2", T0.AddMilliseconds(400), "query orders", spanId: "b", parentSpanId: "a",
                spanStart: T0.AddMilliseconds(100)),
            Entry("3", T0.AddMilliseconds(200), "cache miss", spanId: "b"),
            Entry("4", T0.AddMilliseconds(500), "loose log line")));

        Assert.Equal(["GET /orders", "query orders", "cache miss", "loose log line"],
            timeline.Rows.Select(r => r.Event.Message));
        Assert.Equal([0, 1, 2, 0], timeline.Rows.Select(r => r.Depth));

        Assert.Equal(TimeSpan.FromSeconds(1), timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), timeline.Rows[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(300), timeline.Rows[1].Duration);
        Assert.Null(timeline.Rows[2].Duration); // a log line has no width, only a position
        Assert.Equal(2, timeline.SpanCount);

        // Geometry is a percentage of the whole trace: the child starts 100ms into 1s.
        Assert.Equal(10d, timeline.Rows[1].LeftPercent, 3);
        Assert.Equal(30d, timeline.Rows[1].WidthPercent, 3);
    }

    [Fact]
    public void Falls_back_to_a_flat_chronological_list_without_span_data()
    {
        var timeline = TraceTimeline.Build(Trace(
            Entry("2", T0.AddSeconds(2), "second", appId: "worker"),
            Entry("1", T0, "first"),
            Entry("3", T0.AddSeconds(4), "third")));

        Assert.Equal(["first", "second", "third"], timeline.Rows.Select(r => r.Event.Message));
        Assert.All(timeline.Rows, r => Assert.Equal(0, r.Depth));
        Assert.All(timeline.Rows, r => Assert.Null(r.Duration));
        Assert.Equal(TimeSpan.FromSeconds(2), timeline.Rows[1].Offset);
        Assert.Equal(2, timeline.AppCount);
    }

    [Fact]
    public void A_span_whose_parent_is_not_in_the_trace_becomes_a_root()
    {
        var timeline = TraceTimeline.Build(Trace(
            Entry("1", T0.AddSeconds(1), "orphan", spanId: "b", parentSpanId: "missing", spanStart: T0)));

        Assert.Single(timeline.Rows);
        Assert.Equal(0, timeline.Rows[0].Depth);
    }

    [Fact]
    public void Mutually_parented_spans_do_not_recurse_forever()
    {
        var timeline = TraceTimeline.Build(Trace(
            Entry("1", T0.AddSeconds(1), "a", spanId: "a", parentSpanId: "b", spanStart: T0),
            Entry("2", T0.AddSeconds(1), "b", spanId: "b", parentSpanId: "a", spanStart: T0)));

        Assert.Equal(2, timeline.Rows.Count);
    }

    [Fact]
    public void An_instantaneous_trace_still_draws()
    {
        var timeline = TraceTimeline.Build(Trace(
            Entry("1", T0, "one"),
            Entry("2", T0, "two")));

        Assert.Equal(TimeSpan.Zero, timeline.Duration);
        Assert.All(timeline.Rows, r => Assert.Equal(0d, r.LeftPercent));
    }

    [Fact]
    public void An_empty_trace_has_no_rows()
    {
        var timeline = TraceTimeline.Build(Trace());
        Assert.Empty(timeline.Rows);
        Assert.Equal(0, timeline.AppCount);
    }
}
