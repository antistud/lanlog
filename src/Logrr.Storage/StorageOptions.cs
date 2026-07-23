namespace Logrr.Storage;

/// <summary>Storage + ingest tuning knobs (SPEC §12, the infrastructure half of config).</summary>
public sealed class StorageOptions
{
    /// <summary>Writable data root. Resolved elsewhere; never defaults inside the app folder.</summary>
    public string DataPath { get; set; } = "";

    /// <summary>Global free-space floor; below it the disk guard purges oldest partitions.</summary>
    public long MinFreeDiskMb { get; set; } = 5120;

    /// <summary>Per-app bounded channel capacity before ingest returns 429 (SPEC §4.5).</summary>
    public int ChannelCapacity { get; set; } = 20_000;

    /// <summary>Flush a batch at this many events…</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>…or after this long, whichever comes first.</summary>
    public int FlushIntervalMs { get; set; } = 500;
}
