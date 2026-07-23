namespace Logrr.Contracts;

/// <summary>
/// Capabilities a token carries (SPEC §5.2). Stored as the integer <c>scopes</c> column.
/// </summary>
[Flags]
public enum TokenScopes
{
    None = 0,
    Ingest = 1,
    Read = 2,
}
