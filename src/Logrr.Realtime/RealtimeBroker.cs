using System.Collections.Concurrent;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core;
using Logrr.Core.Filters;

namespace Logrr.Realtime;

/// <summary>Thrown when a subscribe request exceeds a concurrency cap (SPEC §8.5).</summary>
public sealed class SubscriptionRejectedException(string message) : Exception(message);

/// <summary>
/// In-process realtime fan-out (SPEC §8): matches committed events against each
/// subscription's predicate, buffers them, and emits batched frames on a fixed cadence —
/// never one message per event. Single node by design: no bus, no backplane.
/// </summary>
public sealed class RealtimeBroker : IAsyncDisposable
{
    private readonly RealtimeOptions _options;
    private readonly ConcurrentDictionary<string, Subscription> _subs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flusher;

    public RealtimeBroker(RealtimeOptions options)
    {
        _options = options;
        _flusher = Task.Run(() => FlushLoopAsync(_cts.Token));
    }

    public int ActiveCount => _subs.Count;

    /// <summary>
    /// Open a subscription, compiling its filter into a predicate once (SPEC §8.2). Throws
    /// <see cref="SubscriptionRejectedException"/> if a cap is hit, or
    /// <see cref="FilterParseException"/> if the filter is invalid.
    /// </summary>
    public string Subscribe(SubscribeRequest request, string? userId, Func<Frame, Task> sink)
    {
        if (_subs.Count >= _options.MaxSubscriptions)
        {
            throw new SubscriptionRejectedException(
                $"server subscription limit ({_options.MaxSubscriptions}) reached");
        }
        if (userId is not null &&
            _subs.Values.Count(s => s.UserId == userId) >= _options.MaxSubscriptionsPerUser)
        {
            throw new SubscriptionRejectedException(
                $"per-user subscription limit ({_options.MaxSubscriptionsPerUser}) reached");
        }

        var minLevel = LevelMap.ParseOrDefault(request.MinLevel);
        var predicate = BuildPredicate(request.Filter);

        var id = "s_" + Guid.NewGuid().ToString("N")[..8];
        var sub = new Subscription(id, request.AppId, userId, minLevel, predicate,
            _options.SubscriptionBufferSize, sink);
        _subs[id] = sub;
        return id;
    }

    public void Unsubscribe(string id) => _subs.TryRemove(id, out _);

    public void UpdateFilter(string id, string? minLevel, string? filter)
    {
        if (_subs.TryGetValue(id, out var sub))
        {
            sub.UpdateFilter(LevelMap.ParseOrDefault(minLevel), BuildPredicate(filter));
        }
    }

    public void Pause(string id)
    {
        if (_subs.TryGetValue(id, out var sub)) sub.Pause();
    }

    public void Resume(string id)
    {
        if (_subs.TryGetValue(id, out var sub)) sub.Resume();
    }

    /// <summary>Remove subscriptions whose id matches (disconnect / orphan sweep, SPEC §8.5).</summary>
    public void RemoveByPredicate(Func<string, bool> idMatches)
    {
        foreach (var id in _subs.Keys.Where(idMatches).ToList())
        {
            _subs.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Fan a committed batch out to matching subscriptions (SPEC §8.1). Runs post-commit;
    /// only fast in-memory work happens here — buffering, not sending.
    /// </summary>
    public void Publish(string appId, DateOnly day, IReadOnlyList<(long Rowid, LogEvent Event)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var sub in _subs.Values)
        {
            if (sub.AppId != appId)
            {
                continue;
            }

            sub.ObserveTotal(rows.Count);
            foreach (var (rowid, ev) in rows)
            {
                if (ev.Level < sub.MinLevel)
                {
                    continue;
                }
                if (sub.Predicate is { } p && !p(ev))
                {
                    continue;
                }
                sub.Enqueue(ToDto(day, rowid, ev), ev.Level);
            }
        }
    }

    private static Func<LogEvent, bool>? BuildPredicate(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? null : FilterExpression.Parse(filter).Compile();

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_options.FrameIntervalMs);
        var statsEvery = Math.Max(1, 1000 / Math.Max(1, _options.FrameIntervalMs));
        using var timer = new PeriodicTimer(interval);
        var tick = 0L;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                tick++;
                var statsTick = tick % statsEvery == 0;
                var sends = new List<Task>();

                foreach (var sub in _subs.Values)
                {
                    var frame = sub.TakeEventsFrame(_options.MaxEventsPerFrame);
                    if (frame is not null)
                    {
                        sends.Add(SafeSend(sub, frame));
                    }
                    else if (statsTick)
                    {
                        sends.Add(SafeSend(sub, sub.TakeStatsFrame()));
                    }
                }

                if (sends.Count > 0)
                {
                    await Task.WhenAll(sends).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Broker shutting down.
        }
    }

    private static async Task SafeSend(Subscription sub, Frame frame)
    {
        try
        {
            await sub.Sink(frame).ConfigureAwait(false);
        }
        catch
        {
            // A dead client surfaces via hub disconnect, which unsubscribes; ignore here.
        }
    }

    private static LogEventDto ToDto(DateOnly day, long rowid, LogEvent e) => new()
    {
        Id = $"{day:yyyyMMdd}:{rowid}",
        Timestamp = e.Timestamp,
        Level = e.Level,
        Message = e.Message,
        Template = e.Template,
        Exception = e.Exception,
        EventType = e.EventType,
        TraceId = e.TraceId,
        SpanId = e.SpanId,
        Source = e.Source,
        Machine = e.Machine,
        Properties = e.Properties.Count == 0
            ? null
            : JsonSerializer.SerializeToElement(PropertyValueMap(e.Properties)),
    };

    private static Dictionary<string, object?> PropertyValueMap(IReadOnlyDictionary<string, object?> props) =>
        new(props);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _flusher;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
