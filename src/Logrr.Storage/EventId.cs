namespace Logrr.Storage;

/// <summary>
/// The opaque event id used in DTOs and cursors: <c>{yyyyMMdd}:{rowid}</c>. Unique across
/// partitions because rowids are only monotonic <i>within</i> a partition (SPEC §7).
/// </summary>
public static class EventId
{
    public static string Format(DateOnly day, long rowid) => $"{day:yyyyMMdd}:{rowid}";

    public static bool TryParse(string? id, out DateOnly day, out long rowid)
    {
        day = default;
        rowid = 0;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        var colon = id.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        return DateOnly.TryParseExact(id[..colon], "yyyyMMdd", out day)
            && long.TryParse(id[(colon + 1)..], out rowid);
    }
}
