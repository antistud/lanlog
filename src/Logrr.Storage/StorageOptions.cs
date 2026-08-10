namespace Logrr.Storage;

/// <summary>Storage + ingest tuning knobs (SPEC §12, the infrastructure half of config).</summary>
public sealed class StorageOptions
{
    /// <summary>
    /// Writable data root. Resolved elsewhere; never defaults inside the app folder. Still
    /// required with the SQL Server backend: the Data Protection key ring and the internal
    /// log live here regardless of where events go.
    /// </summary>
    public string DataPath { get; set; } = "";

    /// <summary>
    /// SQL Server connection string. Empty (the default) keeps everything in SQLite files
    /// under <see cref="DataPath"/>; setting it moves both the control tables and the event
    /// partitions into that database (SPEC §4.7).
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Schema the SQL Server objects are created in. Ignored by the SQLite backend.</summary>
    public string Schema { get; set; } = "logrr";

    /// <summary>
    /// Global free-space floor; below it the disk guard purges oldest partitions. Applies to
    /// the SQLite backend only — with SQL Server the data is not on this machine's disk.
    /// </summary>
    public long MinFreeDiskMb { get; set; } = 5120;

    /// <summary>Per-app bounded channel capacity before ingest returns 429 (SPEC §4.5).</summary>
    public int ChannelCapacity { get; set; } = 20_000;

    /// <summary>Flush a batch at this many events…</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>…or after this long, whichever comes first.</summary>
    public int FlushIntervalMs { get; set; } = 500;
}
