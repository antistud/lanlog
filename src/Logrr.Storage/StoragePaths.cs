namespace Logrr.Storage;

/// <summary>
/// Resolves on-disk locations under the data root (SPEC §2). One SQLite file per app per
/// UTC day: <c>apps/{appId}/events-{yyyyMMdd}.db</c>.
/// </summary>
public sealed class StoragePaths(string dataRoot)
{
    public string DataRoot { get; } = dataRoot;

    public string ControlDbPath => Path.Combine(DataRoot, "control.db");

    public string KeysDir => Path.Combine(DataRoot, "keys");

    public string AppsDir => Path.Combine(DataRoot, "apps");

    public string AppDir(string appId) => Path.Combine(AppsDir, appId);

    public string PartitionPath(string appId, DateOnly day) =>
        Path.Combine(AppDir(appId), $"events-{day:yyyyMMdd}.db");

    /// <summary>Derive the partition day from an event timestamp (always UTC).</summary>
    public static DateOnly DayOf(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(timestamp.UtcDateTime);

    /// <summary>Ensure the data root and control/keys directories exist.</summary>
    public void EnsureRootDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(KeysDir);
        Directory.CreateDirectory(AppsDir);
    }

    public void EnsureAppDirectory(string appId) => Directory.CreateDirectory(AppDir(appId));

    /// <summary>Enumerate existing partition files for an app, parsed to their day.</summary>
    public IEnumerable<(DateOnly Day, string Path)> ListPartitions(string appId)
    {
        var dir = AppDir(appId);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(dir, "events-????????.db"))
        {
            var name = Path.GetFileNameWithoutExtension(path); // events-yyyyMMdd
            var stamp = name.Length >= 8 ? name[^8..] : "";
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", out var day))
            {
                yield return (day, path);
            }
        }
    }
}
