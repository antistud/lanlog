using System.Collections.Concurrent;
using Logrr.Contracts;
using Logrr.Core;
using Logrr.Realtime;
using Xunit;

namespace Logrr.Tests;

public class RealtimeBrokerTests
{
    private static LogEvent Ev(LogLevel level, string message) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Level = level,
        Message = message,
        Properties = new Dictionary<string, object?>(),
    };

    [Fact]
    public async Task Batches_events_into_frames_not_one_per_event()
    {
        var options = new RealtimeOptions { FrameIntervalMs = 50, MaxEventsPerFrame = 200 };
        await using var broker = new RealtimeBroker(options);

        var frames = new ConcurrentQueue<Frame>();
        broker.Subscribe(new SubscribeRequest { AppId = "billing" }, userId: "u1",
            sink: f => { frames.Enqueue(f); return Task.CompletedTask; });

        var rows = Enumerable.Range(0, 100)
            .Select(i => ((long)i, Ev(LogLevel.Information, $"e{i}")))
            .ToList();
        broker.Publish("billing", new DateOnly(2026, 7, 23), rows);

        await Task.Delay(200);

        var eventFrames = frames.Where(f => f.Type == Frame.EventsType).ToList();
        Assert.NotEmpty(eventFrames);
        // 100 events must arrive in far fewer than 100 frames.
        Assert.True(eventFrames.Count < 5, $"expected batching, got {eventFrames.Count} frames");
        Assert.Equal(100, eventFrames.Sum(f => f.Events.Count));
    }

    [Fact]
    public async Task Server_side_filter_excludes_non_matching_events()
    {
        var options = new RealtimeOptions { FrameIntervalMs = 50 };
        await using var broker = new RealtimeBroker(options);

        var frames = new ConcurrentQueue<Frame>();
        broker.Subscribe(new SubscribeRequest { AppId = "billing", MinLevel = "Error" }, "u1",
            f => { frames.Enqueue(f); return Task.CompletedTask; });

        broker.Publish("billing", new DateOnly(2026, 7, 23),
        [
            (1L, Ev(LogLevel.Information, "noise")),
            (2L, Ev(LogLevel.Error, "real problem")),
        ]);

        await Task.Delay(200);

        var events = frames.Where(f => f.Type == Frame.EventsType).SelectMany(f => f.Events).ToList();
        Assert.Single(events);
        Assert.Equal("real problem", events[0].Message);
    }

    [Fact]
    public async Task Backpressure_drops_oldest_and_reports_count()
    {
        var options = new RealtimeOptions { FrameIntervalMs = 40, MaxEventsPerFrame = 50, SubscriptionBufferSize = 100 };
        await using var broker = new RealtimeBroker(options);

        var frames = new ConcurrentQueue<Frame>();
        broker.Subscribe(new SubscribeRequest { AppId = "billing" }, "u1",
            f => { frames.Enqueue(f); return Task.CompletedTask; });

        // Publish 500 events at once — far beyond the 100-event ring buffer.
        var rows = Enumerable.Range(0, 500).Select(i => ((long)i, Ev(LogLevel.Information, $"e{i}"))).ToList();
        broker.Publish("billing", new DateOnly(2026, 7, 23), rows);

        await Task.Delay(300);

        var dropped = frames.Sum(f => f.Dropped);
        Assert.True(dropped > 0, "overflow should be reported as dropped, not silently lost");
    }

    [Fact]
    public void Enforces_per_user_subscription_cap()
    {
        var options = new RealtimeOptions { MaxSubscriptionsPerUser = 2, FrameIntervalMs = 1000 };
        var broker = new RealtimeBroker(options);
        Func<Frame, Task> sink = _ => Task.CompletedTask;

        broker.Subscribe(new SubscribeRequest { AppId = "a" }, "u1", sink);
        broker.Subscribe(new SubscribeRequest { AppId = "a" }, "u1", sink);
        Assert.Throws<SubscriptionRejectedException>(() =>
            broker.Subscribe(new SubscribeRequest { AppId = "a" }, "u1", sink));
    }
}
