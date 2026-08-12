using Logrr.Storage.Control;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// The live configuration of the Windows Event Log collector (SPEC §6.4). Machines and knobs
/// are rows in the control DB, edited in the admin UI and applied without a restart — the same
/// arrangement <see cref="Ingest.IngestCorsPolicy"/> uses for CORS origins, and for the same
/// reason: this is operational state, not deployment topology.
/// </summary>
/// <remarks>
/// <c>Logrr:WindowsEvents</c> in <c>appsettings.json</c> survives as a <em>seed</em>. The first
/// run that finds an empty settings table imports it, so an existing install keeps collecting
/// exactly what it collected before the upgrade; from then on the file is ignored and the DB is
/// the only source of truth. Two homes for one setting is precisely the split SPEC §12 warns
/// against, so the seeder says out loud when it starts ignoring the file.
/// </remarks>
public sealed class WindowsEventSettings(
    WinlogConfigStore store,
    WinlogCursorStore cursors,
    IConfiguration config,
    Func<DateTimeOffset> clock,
    ILogger<WindowsEventSettings> logger)
{
    /// <summary>
    /// The snapshot the collector polls against. Volatile because the collector reads it on a
    /// pooled thread while the admin UI replaces it on a render thread.
    /// </summary>
    private volatile WindowsEventOptions _current = new();

    /// <summary>
    /// Raised after any save. The collector's timer listens so a shortened poll interval — or
    /// switching collection on — takes effect now rather than after the old interval expires.
    /// </summary>
    public event Action? Changed;

    /// <summary>What the collector should be doing right now.</summary>
    public WindowsEventOptions Current => _current;

    /// <summary>
    /// Seed from configuration if this is the first run since the feature moved into the DB,
    /// then load. Called once during startup bootstrap, after the migrations have run.
    /// </summary>
    public void Initialize()
    {
        if (store.GetSettings() is null)
        {
            SeedFromConfiguration();
        }
        else if (ConfiguredSources().Count > 0)
        {
            logger.LogInformation(
                "Logrr:WindowsEvents lists {Count} source(s) but collection is now configured in the " +
                "database and edited at /admin/windows-events; the configuration section is ignored.",
                ConfiguredSources().Count);
        }
        Reload();
    }

    /// <summary>Every configured machine, including the ones switched off — the admin UI's list.</summary>
    public IReadOnlyList<WinlogSource> Sources() => store.ListSources();

    /// <summary>The global knobs as stored, for the settings form.</summary>
    public WinlogSettings Settings() => store.GetSettings() ?? new WinlogSettings();

    public void SaveSettings(WinlogSettings settings)
    {
        store.SaveSettings(WindowsEventValidation.Clamp(settings), clock());
        Reload();
    }

    /// <summary>
    /// Create or update a machine. Returns null on success, else the reason — the caller shows
    /// it under the form rather than saving something the collector would skip at poll time.
    /// </summary>
    public string? SaveSource(WinlogSource source, bool isNew)
    {
        var machine = source.Machine.Trim();
        if (WindowsEventValidation.MachineError(machine) is { } machineError)
        {
            return machineError;
        }
        if (WindowsEventValidation.AppIdError(source.AppId) is { } appError)
        {
            return appError;
        }
        if (store.MachineTaken(machine, exceptId: isNew ? null : source.Id))
        {
            // One row per machine: two would share its per-channel cursors, so whichever polled
            // second would quietly collect nothing at all.
            return "that machine is already being collected — edit the existing entry instead";
        }

        var record = source with { Machine = machine, AppId = source.AppId.Trim() };
        if (isNew)
        {
            store.CreateSource(record);
        }
        else
        {
            // Renaming a machine strands its cursors under the old name, and the new name seeds
            // from the tail on the next poll — which is what you want, but it means the old rows
            // would linger forever otherwise.
            var previous = store.GetSource(record.Id);
            if (previous is not null && !string.Equals(previous.Machine, machine, StringComparison.OrdinalIgnoreCase))
            {
                cursors.DeleteForMachine(previous.Machine);
            }
            store.UpdateSource(record);
        }
        Reload();
        return null;
    }

    /// <summary>
    /// Stop collecting a machine and forget where it had got to, so re-adding it later starts
    /// cleanly instead of resuming a record id from months ago. The events already collected
    /// stay in their app and are unaffected.
    /// </summary>
    public void DeleteSource(string id)
    {
        if (store.GetSource(id) is { } source)
        {
            cursors.DeleteForMachine(source.Machine);
        }
        store.DeleteSource(id);
        Reload();
    }

    /// <summary>Forget one channel's high-water mark; the next poll seeds it again.</summary>
    public void ResetCursor(string machine, string channel) => cursors.Delete(machine, channel);

    /// <summary>Rebuild the snapshot from the DB and wake anything waiting on it.</summary>
    public void Reload()
    {
        var settings = store.GetSettings() ?? new WinlogSettings();
        _current = new WindowsEventOptions
        {
            Enabled = settings.Enabled,
            PollIntervalSeconds = settings.PollIntervalSeconds,
            MaxEventsPerPoll = settings.MaxEventsPerPoll,
            MaxBatchesPerPoll = settings.MaxBatchesPerPoll,
            InitialBackfillHours = settings.InitialBackfillHours,
            // Disabled rows stay in the table but leave the collector's view entirely, so they
            // are neither polled nor listed as pending on the status page.
            Sources = store.ListSources()
                .Where(s => s.IsEnabled)
                .Select(s => new WindowsEventSourceOptions
                {
                    Machine = s.Machine,
                    AppId = s.AppId,
                    AppName = s.AppName,
                    Channels = s.Channels,
                    MinimumLevel = s.MinimumLevel,
                })
                .ToList(),
        };
        Changed?.Invoke();
    }

    private void SeedFromConfiguration()
    {
        var seed = config.GetSection("Logrr:WindowsEvents").Get<WindowsEventOptions>() ?? new WindowsEventOptions();
        store.SaveSettings(WindowsEventValidation.Clamp(new WinlogSettings
        {
            Enabled = seed.Enabled,
            PollIntervalSeconds = seed.PollIntervalSeconds,
            MaxEventsPerPoll = seed.MaxEventsPerPoll,
            MaxBatchesPerPoll = seed.MaxBatchesPerPoll,
            InitialBackfillHours = seed.InitialBackfillHours,
        }), clock());

        var imported = 0;
        foreach (var src in seed.Sources)
        {
            var machine = src.Machine.Trim();
            var problem = WindowsEventValidation.MachineError(machine)
                          ?? WindowsEventValidation.AppIdError(src.AppId);
            if (problem is not null)
            {
                logger.LogWarning("Skipped Logrr:WindowsEvents source '{Machine}' while seeding: {Reason}",
                    src.Machine, problem);
                continue;
            }
            if (store.MachineTaken(machine))
            {
                logger.LogWarning("Logrr:WindowsEvents lists '{Machine}' more than once; keeping the first entry.",
                    src.Machine);
                continue;
            }
            store.CreateSource(new WinlogSource
            {
                Id = Guid.NewGuid().ToString("N"),
                Machine = machine,
                AppId = src.AppId.Trim(),
                AppName = src.AppName,
                Channels = src.Channels,
                MinimumLevel = src.MinimumLevel,
                IsEnabled = true,
                CreatedUtc = clock(),
            });
            imported++;
        }

        if (imported > 0 || seed.Enabled)
        {
            logger.LogInformation(
                "Imported Windows event collection from configuration: {Count} machine(s), collection {State}. " +
                "It is edited at /admin/windows-events from now on; Logrr:WindowsEvents is no longer read.",
                imported, seed.Enabled ? "on" : "off");
        }
    }

    private IReadOnlyList<WindowsEventSourceOptions> ConfiguredSources() =>
        config.GetSection("Logrr:WindowsEvents").Get<WindowsEventOptions>()?.Sources ?? [];
}
