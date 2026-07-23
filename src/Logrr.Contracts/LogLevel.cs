namespace Logrr.Contracts;

/// <summary>
/// Canonical severity, stored as the integer in the <c>level</c> column (SPEC §5.3).
/// Ordering matters: comparisons like <c>Level &gt;= Warning</c> depend on it.
/// </summary>
public enum LogLevel
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
}
