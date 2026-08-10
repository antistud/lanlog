namespace Logrr.Server.WindowsEvents;

/// <summary>
/// One Windows Event Log record, flattened into plain CLR types. The reader converts
/// <c>EventRecord</c> into this the moment it is read so everything downstream — mapping,
/// cursor handling, the collector loop — is ordinary testable code with no Windows-only
/// types and no native handles to keep alive.
/// </summary>
public sealed record WindowsEventRecord
{
    /// <summary>Monotonic within a channel; the collector's high-water mark. Resets on log clear.</summary>
    public required long RecordId { get; init; }

    public required DateTimeOffset TimeCreated { get; init; }

    /// <summary>Log name, e.g. <c>System</c>.</summary>
    public required string Channel { get; init; }

    /// <summary>Publisher, e.g. <c>Service Control Manager</c>.</summary>
    public required string ProviderName { get; init; }

    public int EventId { get; init; }

    /// <summary>Windows level: 0 LogAlways, 1 Critical, 2 Error, 3 Warning, 4 Information, 5 Verbose.</summary>
    public byte? Level { get; init; }

    public string? LevelDisplayName { get; init; }

    /// <summary>
    /// The rendered description. Null when the publisher's message resources are not available
    /// to the reading machine — common when reading a remote box whose software is not installed
    /// locally — in which case the mapper synthesises a message from the raw data.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>As reported by the record itself, which is authoritative over the configured name.</summary>
    public string? MachineName { get; init; }

    /// <summary>SID of the account the event is attributed to.</summary>
    public string? UserId { get; init; }

    public string? TaskDisplayName { get; init; }

    public string? OpcodeDisplayName { get; init; }

    public string? Keywords { get; init; }

    public Guid? ActivityId { get; init; }

    /// <summary>Publisher-supplied data values, positional and usually unnamed.</summary>
    public IReadOnlyList<string> Data { get; init; } = [];
}

/// <summary>
/// A request for a slice of one channel. Exactly one of <see cref="AfterRecordId"/> and
/// <see cref="Since"/> is set: record id for steady-state polling, time for the first-run
/// backfill before any cursor exists.
/// </summary>
public sealed record WindowsEventQuery
{
    public required string Machine { get; init; }

    public required string Channel { get; init; }

    /// <summary>Return only records with a strictly greater <c>EventRecordID</c>.</summary>
    public long? AfterRecordId { get; init; }

    /// <summary>Return only records at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>
    /// Highest Windows level to return, so the app's minimum-level floor is applied by the
    /// event log rather than by discarding records after transferring them. Null reads everything.
    /// Level 0 (LogAlways) is always returned regardless.
    /// </summary>
    public int? MaxWindowsLevel { get; init; }

    public required int MaxEvents { get; init; }
}

/// <summary>Reads Windows Event Log records. Implemented over RPC; faked in tests.</summary>
public interface IWindowsEventSource
{
    /// <summary>Oldest-first, at most <see cref="WindowsEventQuery.MaxEvents"/> records.</summary>
    IReadOnlyList<WindowsEventRecord> Read(WindowsEventQuery query);

    /// <summary>
    /// The newest <c>EventRecordID</c> in the channel, or null if it is empty. Used to seed a
    /// fresh cursor at the tail and to recognise a cleared log.
    /// </summary>
    long? NewestRecordId(string machine, string channel);
}
