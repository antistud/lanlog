namespace Logrr.Core.Filters;

/// <summary>
/// Resolves what an identifier in a filter expression refers to (SPEC §7.1).
/// </summary>
/// <remarks>
/// A bare identifier is a built-in column when it names one and a property otherwise, which
/// is the terse form the docs and click-to-filter both rely on. The catch is that an event
/// may carry a property whose name collides with a built-in — a client that logs a
/// <c>Machine</c> property is not unusual — and there the bare name silently means the
/// column, so the filter parses, runs, and matches nothing. <see cref="PropertyPrefix"/>
/// gives the property an unambiguous name; <see cref="ForProperty"/> is what UI code that
/// knows it is filtering on a property should build its identifier with.
/// </remarks>
public static class FilterIdent
{
    /// <summary>Qualifier that forces an identifier to resolve to a property.</summary>
    public const string PropertyPrefix = "Properties.";

    private static readonly HashSet<string> BuiltInIdents =
        ["Level", "Message", "Exception", "Source", "TraceId", "SpanId", "Machine"];

    /// <summary>True when the bare identifier names a first-class column.</summary>
    public static bool IsBuiltIn(string ident) => BuiltInIdents.Contains(ident);

    /// <summary>True when the identifier is explicitly qualified as a property.</summary>
    public static bool IsProperty(string ident) =>
        ident.StartsWith(PropertyPrefix, StringComparison.Ordinal);

    /// <summary>The property name an identifier refers to, with any qualifier stripped.</summary>
    public static string PropertyName(string ident) =>
        IsProperty(ident) ? ident[PropertyPrefix.Length..] : ident;

    /// <summary>
    /// The identifier that refers to the property <paramref name="name"/>. Qualified only
    /// when the bare name would be read as a built-in column, so the common case stays
    /// terse (<c>UserId = 1042</c>) and only collisions pay for the disambiguation.
    /// </summary>
    public static string ForProperty(string name) =>
        IsBuiltIn(name) ? PropertyPrefix + name : name;
}
