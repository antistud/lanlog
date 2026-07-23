using System.Text;
using System.Text.Json;

namespace Logrr.Storage;

/// <summary>
/// Encodes/decodes the opaque query cursor <c>{partition date, last rowid}</c> (SPEC §7).
/// Stable under concurrent writes because rowids are monotonic within a date-bounded
/// partition — no OFFSET anywhere.
/// </summary>
public static class CursorCodec
{
    private sealed record CursorData(string P, long I);

    public static string Encode(DateOnly day, long rowid)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new CursorData($"{day:yyyyMMdd}", rowid));
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? cursor, out DateOnly day, out long rowid)
    {
        day = default;
        rowid = 0;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var json = Convert.FromBase64String(b64);
            var data = JsonSerializer.Deserialize<CursorData>(json);
            if (data is null || !DateOnly.TryParseExact(data.P, "yyyyMMdd", out day))
            {
                return false;
            }
            rowid = data.I;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }
}
