using Logrr.Storage.Control;

namespace Logrr.Storage;

/// <summary>
/// The hourly maintenance loop's work (SPEC §4.6): per-app age and size retention, plus the
/// non-negotiable global free-space guard that keeps Logrr from filling the system drive.
/// </summary>
public sealed class RetentionMaintenance(
    PartitionManager partitions,
    StatsReader stats,
    StorageOptions options,
    Action<string>? warn = null)
{
    public sealed record Result(int PartitionsDeleted, bool DiskGuardTripped);

    public Result Run(IReadOnlyList<AppRecord> apps, DateOnly today)
    {
        var deleted = 0;

        foreach (var app in apps)
        {
            // 1. Age: delete partitions older than the app's retention window.
            var cutoff = today.AddDays(-app.RetentionDays);
            foreach (var (day, _) in partitions.Paths.ListPartitions(app.Id).ToList())
            {
                if (day < cutoff)
                {
                    deleted += DeletePartition(app.Id, day);
                }
            }

            // 2. Size: delete oldest until under the app's cap.
            var maxBytes = (long)app.MaxSizeMb * 1024 * 1024;
            var remaining = partitions.Paths.ListPartitions(app.Id).OrderBy(p => p.Day).ToList();
            var totalBytes = remaining.Sum(p => FileLen(p.Path));
            foreach (var (day, path) in remaining)
            {
                if (totalBytes <= maxBytes)
                {
                    break;
                }
                totalBytes -= FileLen(path);
                deleted += DeletePartition(app.Id, day);
            }
        }

        // 3. Global free-space guard: purge oldest across all apps until above the floor.
        var guardTripped = false;
        var minFreeBytes = options.MinFreeDiskMb * 1024 * 1024;
        if (FreeBytes() < minFreeBytes)
        {
            guardTripped = true;
            warn?.Invoke($"Disk free below {options.MinFreeDiskMb} MB — purging oldest partitions across all apps.");

            var all = apps
                .SelectMany(a => partitions.Paths.ListPartitions(a.Id).Select(p => (App: a.Id, p.Day, p.Path)))
                .OrderBy(x => x.Day)
                .ToList();

            foreach (var (appId, day, _) in all)
            {
                if (FreeBytes() >= minFreeBytes)
                {
                    break;
                }
                if (day == today)
                {
                    continue; // never purge the live partition
                }
                deleted += DeletePartition(appId, day);
            }
        }

        return new Result(deleted, guardTripped);
    }

    private int DeletePartition(string appId, DateOnly day)
    {
        partitions.ClosePartition(appId, day);
        var path = partitions.Paths.PartitionPath(appId, day);
        var removed = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                    if (suffix == "")
                    {
                        removed = 1;
                    }
                }
            }
            catch (IOException)
            {
                // A locked or already-removed file — retention retries next cycle.
            }
        }
        return removed;
    }

    private long FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(partitions.Paths.DataRoot));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue; // can't tell → don't purge
        }
    }

    private static long FileLen(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
