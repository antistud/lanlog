using System.IO.Hashing;
using System.Text;

namespace Logrr.Core;

/// <summary>
/// Computes the <c>event_type</c> value: a stable hash of the message template so that a
/// thousand identical exceptions collapse to one group and one ticket (SPEC §4.3, §10.4).
/// </summary>
public static class EventTypeHash
{
    /// <summary>
    /// Hash the template (or the rendered message when no template is present). Uses
    /// XxHash64 for speed on the write path; the result is reinterpreted as a signed
    /// long to fit the <c>event_type INTEGER</c> column.
    /// </summary>
    public static long Compute(string? template, string message)
    {
        var key = !string.IsNullOrEmpty(template) ? template : message;
        var bytes = Encoding.UTF8.GetBytes(key);
        return unchecked((long)XxHash64.HashToUInt64(bytes));
    }
}
