using System.Globalization;
using System.Text.Json;

namespace Logrr.Core;

/// <summary>
/// Normalises property values between the JSON wire form and the scalar CLR objects the
/// filter predicate and rule engine compare against, and back to JSON for storage.
/// </summary>
public static class PropertyValue
{
    /// <summary>
    /// Convert a parsed <see cref="JsonElement"/> to a scalar CLR value. Objects and arrays
    /// are preserved as their raw JSON text (so they round-trip to storage) but are not
    /// meaningfully comparable in filters.
    /// </summary>
    public static object? FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        // Cast to object so the conditional doesn't promote the long branch to double.
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        _ => e.GetRawText(),
    };

    /// <summary>Invariant string form used for rendering and <c>like</c> comparisons.</summary>
    public static string ToInvariantString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Write a normalised scalar back into a JSON object being serialised.</summary>
    public static void Write(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(name);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case bool b:
                writer.WriteBoolean(name, b);
                break;
            case long l:
                writer.WriteNumber(name, l);
                break;
            case int i:
                writer.WriteNumber(name, i);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case float f:
                writer.WriteNumber(name, f);
                break;
            case decimal m:
                writer.WriteNumber(name, m);
                break;
            default:
                writer.WriteString(name, ToInvariantString(value));
                break;
        }
    }

    /// <summary>Serialise a property bag to a compact JSON object for the storage column.</summary>
    public static string ToJson(IReadOnlyDictionary<string, object?> properties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (k, v) in properties)
            {
                Write(writer, k, v);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
