using Logrr.Storage.Control;

namespace Logrr.Storage;

/// <summary>
/// The hourly maintenance loop's work (SPEC §4.6): per-app age and size retention, plus the
/// non-negotiable global free-space guard that keeps Logrr from filling the system drive.
/// </summary>
public sealed class RetentionMaintenance(
    PartitionManager partitions,
    StorageOptions options,
    Action<string>? warn = null)
{
    public sealed record Result(int PartitionsDeleted, bool DiskGuardTripped);

    public Result Run(IReadOnlyList<AppRecord> apps, DateOnly today)
    {
        var deleted = 0;
        var dialect = partitions.Dialect;

        foreach (var app in apps)
        {
            // 1. Age: delete partitions older than the app's retention window.
            var cutoff = today.AddDays(-app.RetentionDays);
            foreach (var day in dialect.PartitionDays(app.Id).ToList())
            {
                if (day < cutoff)
                {
                    deleted += DeletePartition(app.Id, day);
                }
            }

            // 2. Size: delete oldest until under the app's cap.
            var maxBytes = (long)app.MaxSizeMb * 1024 * 1024;
            var remaining = dialect.PartitionDays(app.Id).Order().ToList();
            var totalBytes = remaining.Sum(day => dialect.PartitionSizeBytes(app.Id, day));
            foreach (var day in remaining)
            {
                if (totalBytes <= maxBytes)
                {
                    break;
                }
                totalBytes -= dialect.PartitionSizeBytes(app.Id, day);
                deleted += DeletePartition(app.Id, day);
            }
        }

        // 3. Global free-space guard: purge oldest across all apps until above the floor. Only
        // meaningful when the partitions are files on this machine's disk — with the SQL Server
        // backend the storage is the server's to manage, and the per-app caps above still apply.
        var guardTripped = false;
        var minFreeBytes = options.MinFreeDiskMb * 1024 * 1024;
        if (dialect.SupportsDiskGuard && dialect.FreeBytes() < minFreeBytes)
        {
            guardTripped = true;
            warn?.Invoke($"Disk free below {options.MinFreeDiskMb} MB — purging oldest partitions across all apps.");

            var all = apps
                .SelectMany(a => dialect.PartitionDays(a.Id).Select(day => (App: a.Id, Day: day)))
                .OrderBy(x => x.Day)
                .ToList();

            foreach (var (appId, day) in all)
            {
                if (dialect.FreeBytes() >= minFreeBytes)
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
        return partitions.Dialect.DeletePartition(appId, day) ? 1 : 0;
    }
}
