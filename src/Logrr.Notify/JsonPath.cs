using System.Text.Json;

namespace Logrr.Notify;

/// <summary>
/// Minimal dotted JSON path (<c>$.number</c>, <c>$.html_url</c>, <c>$.a.b[0]</c>) used to
/// pull the ticket id/url out of a destination's response (SPEC §10.1, §10.5).
/// </summary>
public static class JsonPath
{
    public static string? Extract(string json, string? path)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var segment in Segments(path))
            {
                if (segment.Index is { } idx)
                {
                    if (current.ValueKind != JsonValueKind.Array || idx >= current.GetArrayLength())
                    {
                        return null;
                    }
                    current = current[idx];
                }
                else
                {
                    if (current.ValueKind != JsonValueKind.Object ||
                        !current.TryGetProperty(segment.Name!, out current))
                    {
                        return null;
                    }
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => current.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<(string? Name, int? Index)> Segments(string path)
    {
        var p = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..]
              : path.StartsWith('$') ? path[1..] : path;

        foreach (var raw in p.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw;
            var bracket = name.IndexOf('[');
            if (bracket < 0)
            {
                yield return (name, null);
                continue;
            }

            var baseName = name[..bracket];
            if (baseName.Length > 0)
            {
                yield return (baseName, null);
            }

            // one or more [n] suffixes
            var rest = name[bracket..];
            while (rest.StartsWith('[') && rest.IndexOf(']') is var close and > 0)
            {
                if (int.TryParse(rest[1..close], out var idx))
                {
                    yield return (null, idx);
                }
                rest = rest[(close + 1)..];
            }
        }
    }
}
