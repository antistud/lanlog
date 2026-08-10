using Logrr.Core;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Server.WindowsEvents;

/// <summary>
/// Turns a Windows Event Log record into a Logrr event (SPEC §6.4). Pure and platform-agnostic
/// so the mapping decisions below are unit-testable off Windows.
/// </summary>
public static class WindowsEventMapper
{
    /// <summary>Provider data values kept as properties, well under the per-event property cap.</summary>
    private const int MaxDataValues = 20;

    /// <summary>
    /// Windows severity is inverted relative to Logrr's — 1 is the most severe there, 5 here.
    /// Level 0 is "LogAlways", a routing directive rather than a severity, so it lands on
    /// Information rather than pretending to be critical.
    /// </summary>
    public static LogLevel MapLevel(byte? windowsLevel) => windowsLevel switch
    {
        1 => LogLevel.Fatal,        // Critical
        2 => LogLevel.Error,
        3 => LogLevel.Warning,
        5 => LogLevel.Verbose,
        _ => LogLevel.Information,  // 4 Informational, 0 LogAlways, and absent
    };

    /// <summary>
    /// The inverse: the highest Windows level worth reading to satisfy a Logrr minimum-level
    /// floor. Null means "no ceiling — read everything". Debug has no Windows equivalent, so it
    /// behaves like Information; Verbose records are read and then dropped by the floor.
    /// </summary>
    public static int? MaxWindowsLevelFor(LogLevel minimum) => minimum switch
    {
        LogLevel.Fatal => 1,
        LogLevel.Error => 2,
        LogLevel.Warning => 3,
        LogLevel.Information or LogLevel.Debug => 4,
        _ => null,
    };

    /// <summary>
    /// The <c>@mt</c> equivalent, and the only thing that matters for grouping: <see
    /// cref="EventTypeHash"/> keys on the template, so it must identify the *kind* of event and
    /// nothing else. Embedding channel, publisher and event id as literal text gives one group
    /// per distinct Windows event; putting the description here instead would either scatter
    /// every occurrence into its own group or, if left as a shared placeholder, collapse the
    /// entire event log into one. Square brackets, not braces, so it is never mistaken for a
    /// message template with holes.
    /// </summary>
    public static string TemplateFor(WindowsEventRecord record) =>
        $"[{record.Channel}/{record.ProviderName} {record.EventId}]";

    public static LogEvent ToLogEvent(WindowsEventRecord record, string configuredMachine)
    {
        var template = TemplateFor(record);
        var message = BuildMessage(record);

        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Channel"] = record.Channel,
            ["ProviderName"] = record.ProviderName,
            ["EventId"] = (long)record.EventId,
            ["RecordId"] = record.RecordId,
        };

        // Mirrors the CLEF path, where MachineName is both a property and the Machine column.
        var machine = Blank(record.MachineName) ? NormalizeMachine(configuredMachine) : record.MachineName!;
        props["MachineName"] = machine;

        AddIfPresent(props, "WinLevel", record.LevelDisplayName);
        AddIfPresent(props, "UserId", record.UserId);
        AddIfPresent(props, "Task", record.TaskDisplayName);
        AddIfPresent(props, "Opcode", record.OpcodeDisplayName);
        AddIfPresent(props, "Keywords", record.Keywords);

        for (var i = 0; i < record.Data.Count && i < MaxDataValues; i++)
        {
            AddIfPresent(props, "Data" + i, record.Data[i]);
        }

        return new LogEvent
        {
            Timestamp = record.TimeCreated,
            Level = MapLevel(record.Level),
            Template = template,
            Message = message,
            EventType = EventTypeHash.Compute(template, message),
            // Publisher in the Source column so `Source = '...'` filters and rules work the
            // same way they do for SourceContext on application logs.
            Source = record.ProviderName,
            Machine = machine,
            TraceId = record.ActivityId is { } id && id != Guid.Empty ? id.ToString("n") : null,
            Properties = props,
        };
    }

    /// <summary>
    /// The rendered description when the publisher's message resources resolved, otherwise a
    /// synthesised line from the raw data — an unresolvable provider must still produce a
    /// readable, greppable event rather than an empty message.
    /// </summary>
    private static string BuildMessage(WindowsEventRecord record)
    {
        var description = record.Description?.Trim();
        if (!Blank(description))
        {
            return description!;
        }

        var data = record.Data.Where(d => !Blank(d)).Take(MaxDataValues).ToList();
        var suffix = data.Count > 0 ? ": " + string.Join(" | ", data) : "";
        return $"{record.ProviderName} event {record.EventId}{suffix}";
    }

    private static void AddIfPresent(Dictionary<string, object?> props, string name, string? value)
    {
        if (!Blank(value))
        {
            props[name] = value!.Trim();
        }
    }

    /// <summary>Turn the local-machine aliases into something worth showing in the Machine column.</summary>
    public static string NormalizeMachine(string machine)
    {
        var text = machine.Trim();
        return IsLocal(text) ? Environment.MachineName : text;
    }

    public static bool IsLocal(string machine) =>
        string.IsNullOrWhiteSpace(machine) ||
        machine is "." or "localhost" or "127.0.0.1" or "::1" ||
        string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
