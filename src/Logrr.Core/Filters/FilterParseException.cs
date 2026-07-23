namespace Logrr.Core.Filters;

/// <summary>
/// Thrown when a filter expression does not parse. The query and subscription layers turn
/// this into a 400 / inline validation error — the parser never falls through to string
/// concatenation (SPEC §7.1).
/// </summary>
public sealed class FilterParseException(string message, int position) : Exception(message)
{
    /// <summary>Character offset in the source expression where parsing failed.</summary>
    public int Position { get; } = position;
}
