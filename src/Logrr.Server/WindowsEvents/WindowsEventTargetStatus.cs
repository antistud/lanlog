namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Live collection state for one (machine, channel), as shown on the admin status page.
/// One row per configured target — including targets that have never been reachable, since
/// those are the ones worth looking at.
/// </summary>
public sealed record WindowsEventTargetStatus
{
    /// <summary>Display name, with <c>.</c> / <c>localhost</c> resolved to the actual host.</summary>
    public required string Machine { get; init; }

    /// <summary>Exactly as written in configuration, so an operator can find the entry.</summary>
    public required string ConfiguredMachine { get; init; }

    public required string Channel { get; init; }

    public required string AppId { get; init; }

    /// <summary>High-water mark; null before the channel has ever been polled.</summary>
    public long? LastRecordId { get; init; }

    public DateTimeOffset? CursorUpdatedUtc { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public int ConsecutiveFailures { get; init; }

    public string? LastError { get; init; }

    /// <summary>Events collected since the server started. Not persisted — a restart resets it.</summary>
    public long EventsCollected { get; init; }

    /// <summary>
    /// Healthy means "last attempt succeeded". A target that has never been attempted is not
    /// yet failing, so it reads as pending rather than broken.
    /// </summary>
    public bool IsFailing => ConsecutiveFailures > 0;

    public bool IsPending => LastAttemptUtc is null;
}
