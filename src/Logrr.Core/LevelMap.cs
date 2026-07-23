using Logrr.Contracts;

namespace Logrr.Core;

/// <summary>
/// Maps level strings from Serilog and Microsoft.Extensions.Logging onto the canonical
/// numeric <see cref="LogLevel"/> (SPEC §5.3). Unrecognised strings map to Information and
/// the original is preserved by the caller as a <c>_rawLevel</c> property.
/// </summary>
public static class LevelMap
{
    private static readonly Dictionary<string, LogLevel> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Serilog
            ["Verbose"] = LogLevel.Verbose,
            ["Debug"] = LogLevel.Debug,
            ["Information"] = LogLevel.Information,
            ["Warning"] = LogLevel.Warning,
            ["Error"] = LogLevel.Error,
            ["Fatal"] = LogLevel.Fatal,
            // Microsoft.Extensions.Logging aliases
            ["Trace"] = LogLevel.Verbose,
            ["Info"] = LogLevel.Information,
            ["Warn"] = LogLevel.Warning,
            ["Critical"] = LogLevel.Fatal,
        };

    /// <summary>Absent level means Information (SPEC §6.1).</summary>
    public const LogLevel Default = LogLevel.Information;

    /// <summary>
    /// Resolve a level token. Accepts a name, a MEL alias, or a numeric string 0..5.
    /// Returns false for anything unrecognised (caller falls back to Information).
    /// </summary>
    public static bool TryParse(string? value, out LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            level = Default;
            return false;
        }

        if (ByName.TryGetValue(value, out level))
        {
            return true;
        }

        if (int.TryParse(value, out var n) && n is >= 0 and <= 5)
        {
            level = (LogLevel)n;
            return true;
        }

        level = Default;
        return false;
    }

    /// <summary>Parse or fall back to Information, never throwing.</summary>
    public static LogLevel ParseOrDefault(string? value) =>
        TryParse(value, out var level) ? level : Default;

    /// <summary>The canonical name used on the wire and in filter values.</summary>
    public static string ToName(LogLevel level) => level.ToString();
}
