using System.Globalization;
using System.Text;

namespace Logrr.Core;

/// <summary>
/// Renders a Serilog-style message template against a property bag, used to produce
/// <c>@m</c> server-side when a CLEF line omits it (SPEC §6.1).
/// </summary>
/// <remarks>
/// Supports the common surface: named holes <c>{Name}</c>, destructuring/stringify hints
/// <c>{@Name}</c> / <c>{$Name}</c>, positional holes <c>{0}</c>, brace escaping
/// <c>{{</c> / <c>}}</c>, and an optional format specifier <c>{Name:format}</c>. Alignment
/// is parsed and ignored. It is a renderer, not a validator — an unmatched hole renders as
/// its own name so nothing is silently lost.
/// </remarks>
public static class MessageTemplate
{
    public static string Render(string template, IReadOnlyDictionary<string, object?> properties)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];

            if (c == '{')
            {
                // Escaped "{{" → literal "{".
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    // Unterminated hole: emit the rest literally.
                    sb.Append(template, i, template.Length - i);
                    break;
                }

                var hole = template.Substring(i + 1, close - i - 1);
                sb.Append(RenderHole(hole, properties));
                i = close + 1;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                // Escaped "}}" → literal "}".
                sb.Append('}');
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>A piece of a tokenized template: literal text, or a named hole to be filled.</summary>
    public readonly record struct Segment(bool IsHole, string Text)
    {
        /// <summary>For a hole, the resolved property name (destructuring/format hints stripped).</summary>
        public string Name => Text;
    }

    /// <summary>
    /// Split a template into literal and hole segments so a UI can render hole values with
    /// distinct styling (and make them click-to-filter). Brace escapes are unescaped into the
    /// literal runs; holes carry their bare property name. Value resolution is left to the caller.
    /// </summary>
    public static IReadOnlyList<Segment> Tokenize(string? template)
    {
        var segments = new List<Segment>();
        if (string.IsNullOrEmpty(template))
        {
            return segments;
        }

        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                segments.Add(new Segment(false, literal.ToString()));
                literal.Clear();
            }
        }

        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    literal.Append(template, i, template.Length - i);
                    break;
                }
                FlushLiteral();
                segments.Add(new Segment(true, HoleName(template.Substring(i + 1, close - i - 1))));
                i = close + 1;
                continue;
            }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i += 2;
                continue;
            }
            literal.Append(c);
            i++;
        }
        FlushLiteral();
        return segments;
    }

    /// <summary>Reduce a raw hole body (<c>@Name,align:format</c>) to its bare property name.</summary>
    private static string HoleName(string hole)
    {
        var name = hole;
        var colon = hole.IndexOf(':');
        var comma = hole.IndexOf(',');
        var cut = colon < 0 ? comma : comma < 0 ? colon : Math.Min(colon, comma);
        if (cut >= 0)
        {
            name = hole[..cut];
        }
        if (name.Length > 0 && (name[0] == '@' || name[0] == '$'))
        {
            name = name[1..];
        }
        return name;
    }

    private static string RenderHole(string hole, IReadOnlyDictionary<string, object?> properties)
    {
        if (hole.Length == 0)
        {
            return "{}";
        }

        // Split off format/alignment: {Name,align:format}.
        string name = hole;
        string? format = null;
        var colon = hole.IndexOf(':');
        var comma = hole.IndexOf(',');
        var cut = colon < 0 ? comma : comma < 0 ? colon : Math.Min(colon, comma);
        if (cut >= 0)
        {
            name = hole[..cut];
            if (colon >= 0)
            {
                format = hole[(colon + 1)..];
            }
        }

        // Strip destructuring hints.
        if (name.Length > 0 && (name[0] == '@' || name[0] == '$'))
        {
            name = name[1..];
        }

        if (!properties.TryGetValue(name, out var value) || value is null)
        {
            // Unmatched hole renders as its bare name — visible, not swallowed.
            return name;
        }

        if (format is not null && value is IFormattable f)
        {
            return f.ToString(format, CultureInfo.InvariantCulture);
        }

        return PropertyValue.ToInvariantString(value);
    }
}
