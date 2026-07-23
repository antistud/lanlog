using System.Text;
using System.Text.Json;

namespace Logrr.Core;

/// <summary>
/// The ~150-line <c>{{token}}</c> body substituter for webhook payloads (SPEC §10.2),
/// deliberately in-house to own its escaping semantics rather than take a Handlebars
/// dependency.
/// </summary>
/// <remarks>
/// Escaping default: in JSON mode, every token substituted <i>inside a string literal</i>
/// is JSON-string-escaped exactly once, so a stack trace full of quotes and newlines can
/// neither break the payload nor inject structure. A token outside any string literal
/// (e.g. a bare numeric value) is not escaped unless it carries the explicit <c>| json</c>
/// filter. Callers should still validate the rendered body parses as JSON before dispatch.
/// </remarks>
public static class WebhookTemplate
{
    /// <summary>
    /// Render <paramref name="template"/>, resolving each token path through
    /// <paramref name="resolve"/> (returns null for unknown paths). When
    /// <paramref name="jsonMode"/> is true, applies the string-literal escaping rule above.
    /// </summary>
    public static string Render(string template, Func<string, string?> resolve, bool jsonMode)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(resolve);

        var sb = new StringBuilder(template.Length + 32);
        var inString = false;
        var i = 0;

        while (i < template.Length)
        {
            var c = template[i];

            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }

                var token = template.Substring(i + 2, end - (i + 2)).Trim();
                sb.Append(RenderToken(token, resolve, jsonMode, inString));
                i = end + 2;
                continue;
            }

            // Track JSON string-literal context (respecting backslash escapes).
            if (jsonMode && c == '"' && !IsEscaped(template, i))
            {
                inString = !inString;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsEscaped(string s, int quoteIndex)
    {
        var backslashes = 0;
        for (var j = quoteIndex - 1; j >= 0 && s[j] == '\\'; j--)
        {
            backslashes++;
        }
        return (backslashes & 1) == 1;
    }

    private static string RenderToken(string token, Func<string, string?> resolve, bool jsonMode, bool inString)
    {
        // path | filter | filter:arg
        var segments = SplitPipes(token);
        var path = segments[0].Trim();
        var value = resolve(path) ?? string.Empty;

        var forceEscape = false;
        for (var k = 1; k < segments.Count; k++)
        {
            var (name, arg) = ParseFilter(segments[k]);
            switch (name)
            {
                case "json":
                    forceEscape = true; // escaping happens once, below
                    break;
                case "upper":
                    value = value.ToUpperInvariant();
                    break;
                case "truncate":
                    if (int.TryParse(arg, out var max) && value.Length > max)
                    {
                        value = value[..max] + "…";
                    }
                    break;
                case "md":
                    value = value.Length == 0 ? value : $"```\n{value}\n```";
                    break;
                case "default":
                    if (string.IsNullOrEmpty(value))
                    {
                        value = arg ?? string.Empty;
                    }
                    break;
                // Unknown filters pass the value through unchanged.
            }
        }

        var escape = forceEscape || (jsonMode && inString);
        return escape ? JsonEscape(value) : value;
    }

    /// <summary>JSON-string-escape without adding surrounding quotes.</summary>
    private static string JsonEscape(string value) => JsonEncodedText.Encode(value).ToString();

    private static List<string> SplitPipes(string token)
    {
        // Split on '|' but not inside a quoted filter argument.
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in token)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
                sb.Append(c);
            }
            else if (c == '|' && !inQuote)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        parts.Add(sb.ToString());
        return parts;
    }

    private static (string Name, string? Arg) ParseFilter(string segment)
    {
        var s = segment.Trim();
        var colon = s.IndexOf(':');
        if (colon < 0)
        {
            return (s, null);
        }

        var name = s[..colon].Trim();
        var arg = s[(colon + 1)..].Trim();
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
        {
            arg = arg[1..^1];
        }
        return (name, arg);
    }
}
