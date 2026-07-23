using Logrr.Contracts;
using Logrr.Core;

namespace Logrr.Realtime;

/// <summary>
/// One live-tail subscription (SPEC §8.2–8.4): a compiled predicate, a bounded ring buffer
/// that drops oldest under backpressure, and per-frame rate accounting. All mutating access
/// is serialised on <see cref="_gate"/> — publish (writer thread) and flush (batcher thread)
/// both touch it.
/// </summary>
internal sealed class Subscription
{
    private readonly Lock _gate = new();
    private readonly Queue<LogEventDto> _buffer = new();
    private readonly int _capacity;

    private int _dropped;
    private int _matched;
    private int _total;
    private readonly Dictionary<string, int> _byLevel = new();
    private long _seq;

    public Subscription(string id, string appId, string? userId, LogLevel minLevel,
        Func<LogEvent, bool>? predicate, int capacity, Func<Frame, Task> sink)
    {
        Id = id;
        AppId = appId;
        UserId = userId;
        MinLevel = minLevel;
        Predicate = predicate;
        _capacity = capacity;
        Sink = sink;
    }

    public string Id { get; }
    public string AppId { get; }
    public string? UserId { get; }
    public LogLevel MinLevel { get; private set; }
    public Func<LogEvent, bool>? Predicate { get; private set; }
    public Func<Frame, Task> Sink { get; }
    public bool Paused { get; private set; }

    public void UpdateFilter(LogLevel minLevel, Func<LogEvent, bool>? predicate)
    {
        lock (_gate)
        {
            MinLevel = minLevel;
            Predicate = predicate;
        }
    }

    public void Pause() { lock (_gate) { Paused = true; } }
    public void Resume() { lock (_gate) { Paused = false; } }

    /// <summary>Count every app event this subscription observed, matched or not.</summary>
    public void ObserveTotal(int count)
    {
        lock (_gate)
        {
            _total += count;
        }
    }

    /// <summary>Buffer a matched event, dropping the oldest if the ring is full.</summary>
    public void Enqueue(LogEventDto dto, LogLevel level)
    {
        lock (_gate)
        {
            _matched++;
            var name = level.ToString();
            _byLevel[name] = _byLevel.GetValueOrDefault(name) + 1;

            _buffer.Enqueue(dto);
            while (_buffer.Count > _capacity)
            {
                _buffer.Dequeue();
                _dropped++;
            }
        }
    }

    /// <summary>
    /// Build the next events frame (up to <paramref name="maxEvents"/>), or null when there
    /// is nothing to send or the subscription is paused. Resets the per-frame counters.
    /// </summary>
    public Frame? TakeEventsFrame(int maxEvents)
    {
        lock (_gate)
        {
            if (Paused || _buffer.Count == 0)
            {
                return null;
            }

            var take = Math.Min(maxEvents, _buffer.Count);
            var events = new List<LogEventDto>(take);
            for (var i = 0; i < take; i++)
            {
                events.Add(_buffer.Dequeue());
            }

            return new Frame
            {
                Type = Frame.EventsType,
                SubscriptionId = Id,
                Seq = ++_seq,
                Events = events,
                Dropped = TakeDropped(),
                Rate = TakeRate(),
            };
        }
    }

    /// <summary>Build a periodic stats-only frame so counters move even on a quiet app.</summary>
    public Frame TakeStatsFrame()
    {
        lock (_gate)
        {
            return new Frame
            {
                Type = Frame.StatsType,
                SubscriptionId = Id,
                Seq = ++_seq,
                Events = [],
                Dropped = TakeDropped(),
                Rate = TakeRate(),
            };
        }
    }

    public bool HasPendingEvents()
    {
        lock (_gate)
        {
            return !Paused && _buffer.Count > 0;
        }
    }

    private int TakeDropped()
    {
        var d = _dropped;
        _dropped = 0;
        return d;
    }

    private RateSummary TakeRate()
    {
        var rate = new RateSummary
        {
            Matched = _matched,
            Total = _total,
            ByLevel = new Dictionary<string, int>(_byLevel),
        };
        _matched = 0;
        _total = 0;
        _byLevel.Clear();
        return rate;
    }
}
