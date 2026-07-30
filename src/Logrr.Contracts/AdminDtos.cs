namespace Logrr.Contracts;

/// <summary>App summary for the overview and API list (SPEC §7, §9).</summary>
public sealed record AppDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int RetentionDays { get; init; }
    public int MaxSizeMb { get; init; }
    public LogLevel MinimumLevel { get; init; }
    public IReadOnlyList<string> IndexedProperties { get; init; } = [];
    public bool IsEnabled { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>Per-app counters (SPEC §7 stats endpoint).</summary>
public sealed record AppStatsDto
{
    public required string AppId { get; init; }
    public long TotalEvents { get; init; }
    public IReadOnlyDictionary<string, long> CountByLevel { get; init; } =
        new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> CountByHour { get; init; } =
        new Dictionary<string, long>();
    public long StorageBytes { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }

    /// <summary>Error + Fatal events not covered by an acknowledgement — what the overview alerts on.</summary>
    public long UnacknowledgedErrors { get; init; }
}

/// <summary>Liveness + queue/disk snapshot (SPEC §13). 503 when the disk guard trips.</summary>
public sealed record HealthDto
{
    public required string Status { get; init; }
    public int QueueDepth { get; init; }
    public long DroppedLastHour { get; init; }
    public long DiskFreeMb { get; init; }
    public long OldestUnflushedMs { get; init; }
    public int ActiveSubscriptions { get; init; }
    public int PendingDeliveries { get; init; }
    public int DeadLettered { get; init; }
}

/// <summary>Build metadata (SPEC §7 buildinfo endpoint).</summary>
public sealed record BuildInfoDto
{
    public required string Version { get; init; }
    public string? Commit { get; init; }
    public DateTimeOffset? BuildDate { get; init; }
}
