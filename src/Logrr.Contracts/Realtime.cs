namespace Logrr.Contracts;

/// <summary>Client request to open a live-tail subscription (SPEC §8.2).</summary>
public sealed record SubscribeRequest
{
    public required string AppId { get; init; }

    /// <summary>Minimum level name; events below are not shipped.</summary>
    public string? MinLevel { get; init; }

    /// <summary>Filter expression (SPEC §7.1), compiled server-side into a predicate.</summary>
    public string? Filter { get; init; }

    public int? MaxEventsPerSecond { get; init; }
}

/// <summary>Per-frame rate summary (SPEC §8.3).</summary>
public sealed record RateSummary
{
    /// <summary>Events matching the subscription filter in the frame window.</summary>
    public int Matched { get; init; }

    /// <summary>All events seen for the app in the window, matched or not.</summary>
    public int Total { get; init; }

    public IReadOnlyDictionary<string, int> ByLevel { get; init; } =
        new Dictionary<string, int>();
}

/// <summary>
/// A batched push. Never one message per event — frames flush on interval or count
/// (SPEC §8.3). <see cref="Type"/> is "events" or "stats".
/// </summary>
public sealed record Frame
{
    public required string Type { get; init; }

    public required string SubscriptionId { get; init; }

    /// <summary>Monotonic per-subscription sequence, so clients can detect gaps.</summary>
    public long Seq { get; init; }

    public IReadOnlyList<LogEventDto> Events { get; init; } = [];

    /// <summary>Count dropped by backpressure since the last frame (SPEC §8.4).</summary>
    public int Dropped { get; init; }

    public RateSummary? Rate { get; init; }

    public const string EventsType = "events";
    public const string StatsType = "stats";
}
