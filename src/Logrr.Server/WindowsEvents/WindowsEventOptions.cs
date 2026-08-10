using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Agentless Windows Event Log collection (SPEC §6.4), bound from <c>Logrr:WindowsEvents</c>.
/// Off by default. The server reads the event log itself — over RPC for remote machines — so
/// nothing has to be installed on the boxes being collected, which is the whole point.
/// </summary>
public sealed class WindowsEventOptions
{
    /// <summary>Run the collector. Requires a Windows host; ignored (with a warning) elsewhere.</summary>
    public bool Enabled { get; init; }

    /// <summary>Seconds between polls. The event log is pull-only over RPC, so this is the latency floor.</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>Records read per query. Larger batches catch up faster but hold more memory.</summary>
    public int MaxEventsPerPoll { get; init; } = 500;

    /// <summary>
    /// How many full batches one poll will chase before yielding. Bounds the work a single
    /// tick can do when the collector is far behind, so one noisy machine cannot starve the rest.
    /// </summary>
    public int MaxBatchesPerPoll { get; init; } = 10;

    /// <summary>
    /// History to pull the first time a channel is seen. Zero (the default) starts at the tail
    /// and ships only new events. Clamped to 720 h because ingest rejects anything older than
    /// 30 days (SPEC §6.3), so a larger value would silently discard the excess.
    /// </summary>
    public int InitialBackfillHours { get; init; }

    public IReadOnlyList<WindowsEventSourceOptions> Sources { get; init; } = [];

    public const int MaxInitialBackfillHours = 720;
}

/// <summary>One collected machine and the channels to read from it.</summary>
public sealed class WindowsEventSourceOptions
{
    /// <summary>
    /// NetBIOS name, FQDN, or <c>.</c> / <c>localhost</c> for the Logrr host itself. Remote
    /// machines are read with the server process's own identity — see docs/SETUP.md §9.
    /// </summary>
    public string Machine { get; init; } = ".";

    /// <summary>Logrr app the events land in. Auto-created if it does not exist; must be a slug.</summary>
    public string AppId { get; init; } = "windows";

    /// <summary>Display name used only when auto-creating the app.</summary>
    public string? AppName { get; init; }

    /// <summary>Log names to read, e.g. <c>Application</c>, <c>System</c>, <c>Security</c>.</summary>
    public IReadOnlyList<string> Channels { get; init; } = ["Application", "System"];

    /// <summary>
    /// Minimum level used only when auto-creating the app. Afterwards the app's own setting
    /// (editable in the UI) governs, and the collector narrows its query to match it — so
    /// raising the floor stops the events being read at all, not just written.
    /// </summary>
    public LogLevel? MinimumLevel { get; init; }
}
