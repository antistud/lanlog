using System.Text.RegularExpressions;
using Logrr.Storage.Control;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Agentless Windows Event Log collection (SPEC §6.4) as the collector sees it: a snapshot of
/// the <c>winlog_settings</c> and <c>winlog_sources</c> rows, rebuilt by
/// <see cref="WindowsEventSettings"/> whenever the admin UI changes them. Off by default. The
/// server reads the event log itself — over RPC for remote machines — so nothing has to be
/// installed on the boxes being collected, which is the whole point.
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

    /// <summary>
    /// Log names to read, e.g. <c>Application</c>, <c>System</c>, <c>Security</c>. Empty means
    /// the default pair — read <see cref="EffectiveChannels"/>, never this, when collecting.
    /// </summary>
    /// <remarks>
    /// Deliberately defaulted to empty rather than to the pair itself: the configuration binder
    /// *appends* to a collection property's existing contents instead of replacing them, so a
    /// non-empty default would leave anyone who sets <c>Channels</c> collecting the defaults as
    /// well as their own list — every channel twice.
    /// </remarks>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>
    /// The channels actually collected: the configured list, or the default pair when it is
    /// empty. De-duplicated, because listing a channel twice would poll it twice for nothing.
    /// </summary>
    public IReadOnlyList<string> EffectiveChannels =>
        (Channels.Count == 0 ? DefaultChannels : Channels)
        .Where(c => !string.IsNullOrWhiteSpace(c))
        .Select(c => c.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static readonly IReadOnlyList<string> DefaultChannels = ["Application", "System"];

    /// <summary>
    /// Minimum level used only when auto-creating the app. Afterwards the app's own setting
    /// (editable in the UI) governs, and the collector narrows its query to match it — so
    /// raising the floor stops the events being read at all, not just written.
    /// </summary>
    public LogLevel? MinimumLevel { get; init; }
}

/// <summary>
/// The rules a collection source has to satisfy, in one place: the admin UI checks them before
/// saving, the config seeder checks them before importing, and the collector checks the app id
/// again at poll time because a row written by an older build still has to be survivable.
/// </summary>
public static partial class WindowsEventValidation
{
    public const int MinPollIntervalSeconds = 5;
    public const int MaxPollIntervalSeconds = 3600;

    /// <summary>Null when the machine is usable, else the message to put under the field.</summary>
    public static string? MachineError(string? machine)
    {
        var value = machine?.Trim() ?? "";
        if (value.Length == 0)
        {
            return "enter a machine name, or . for this server";
        }
        if (value.Length > 255)
        {
            return "machine name is too long";
        }
        // Anything with a separator in it is a path or an account, not a machine — and would
        // fail at the event log API with a far less obvious error.
        return value.Any(c => char.IsWhiteSpace(c) || c is '\\' or '/' or ':')
            ? "just the machine name, e.g. WEB01 or web01.contoso.com"
            : null;
    }

    /// <summary>Null when the app id is a usable slug, else the message to put under the field.</summary>
    public static string? AppIdError(string? appId) =>
        IsValidAppId(appId?.Trim()) ? null : "3–32 characters, lower-case letters, digits and hyphens only";

    public static bool IsValidAppId(string? appId) =>
        appId is not null && AppIdPattern().IsMatch(appId);

    /// <summary>
    /// Split the UI's comma-separated channel box into a list. Empty is legal and means the
    /// collector's default pair; duplicates are dropped because a channel listed twice would be
    /// polled twice for nothing.
    /// </summary>
    public static IReadOnlyList<string> ParseChannels(string? input) =>
        (input ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Hold the global knobs to ranges the collector can actually honour, so what the settings
    /// page shows after a save is what the next poll will do.
    /// </summary>
    public static WinlogSettings Clamp(WinlogSettings s) => s with
    {
        PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, MinPollIntervalSeconds, MaxPollIntervalSeconds),
        MaxEventsPerPoll = Math.Clamp(s.MaxEventsPerPoll, 1, 10_000),
        MaxBatchesPerPoll = Math.Clamp(s.MaxBatchesPerPoll, 1, 1_000),
        InitialBackfillHours = Math.Clamp(s.InitialBackfillHours, 0, WindowsEventOptions.MaxInitialBackfillHours),
    };

    [GeneratedRegex("^[a-z0-9-]{3,32}$")]
    private static partial Regex AppIdPattern();
}
