namespace Logrr.Server.Exploring;

/// <summary>
/// A right-hand side to filter on: the field name and its already-formatted value literal
/// (a bare number/bool/level, or a quoted string). Produced by click-to-filter affordances.
/// </summary>
public readonly record struct FieldFilter(string Field, string Rendered);

/// <summary>
/// Builds filter-expression clauses for click-to-filter, appending to whatever the user has
/// already typed. The output is valid input to <c>FilterExpression.Parse</c> (SPEC §7.1).
/// </summary>
public static class FilterClause
{
    /// <summary>Append <c>field = value</c> (or <c>not field = value</c>) with <c>and</c>.</summary>
    public static string Add(string? existing, FieldFilter f, bool negate)
    {
        var clause = negate ? $"not {f.Field} = {f.Rendered}" : $"{f.Field} = {f.Rendered}";
        return string.IsNullOrWhiteSpace(existing) ? clause : $"{existing.Trim()} and {clause}";
    }

    /// <summary>Single-quote and escape a string value for the filter language.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
