namespace Logrr.Core.Filters;

/// <summary>
/// Which SQL flavour <see cref="FilterExpression.ToSql"/> emits. The parser and the in-memory
/// predicate are shared; only the generated text differs, and it differs in ways that exist
/// purely to keep the two backends agreeing (SPEC §7.1).
/// </summary>
/// <remarks>
/// The invariant being defended is that a filter selects the same rows in SQL as the compiled
/// predicate accepts in memory. SQLite gives that almost for free: <c>json_extract</c> returns
/// typed JSON values, string comparison is ordinal, and <c>LIKE</c> is ASCII-case-insensitive
/// — exactly what <c>Compile()</c> does. SQL Server matches none of those defaults, so the
/// <see cref="SqlServer"/> emitter has to ask for each of them explicitly: numbers cast out of
/// <c>JSON_VALUE</c>'s nvarchar, comparisons forced to a binary collation, and <c>LIKE</c>
/// forced to a case-insensitive one.
/// </remarks>
public enum FilterSqlDialect
{
    Sqlite = 0,
    SqlServer = 1,
}
