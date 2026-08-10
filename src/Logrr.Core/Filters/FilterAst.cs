using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Logrr.Core.Filters;

internal enum CompareOp { Eq, Neq, Gt, Gte, Lt, Lte, Like, IsNull, IsNotNull }

internal enum ValueKind { Number, String, Bool }

/// <summary>A parsed right-hand-side literal.</summary>
internal readonly record struct FilterValue(ValueKind Kind, object Raw)
{
    public double AsNumber => Kind == ValueKind.Number ? (double)Raw : 0;
    public string AsString => Raw is string s ? s : PropertyValue.ToInvariantString(Raw);
    public bool AsBool => Kind == ValueKind.Bool && (bool)Raw;
}

/// <summary>Accumulates SQL text and ordered parameters for the query backend.</summary>
internal sealed class SqlBuilder(FilterSqlDialect dialect)
{
    private readonly StringBuilder _sql = new();
    private readonly List<object?> _params = [];

    public FilterSqlDialect Dialect { get; } = dialect;

    public void Append(string text) => _sql.Append(text);

    public string AddParam(object? value)
    {
        var name = "@p" + _params.Count;
        _params.Add(value);
        return name;
    }

    public string Sql => _sql.ToString();
    public IReadOnlyList<object?> Parameters => _params;
}

/// <summary>
/// Base of the filter AST. Every node emits to SQL (<see cref="ToSql"/>) and evaluates
/// in memory (<see cref="Evaluate"/>); the two must agree exactly (SPEC §7.1).
/// </summary>
/// <remarks>
/// <see cref="Evaluate"/> returns a <see cref="Nullable{Boolean}"/> to model SQL's
/// three-valued logic. A comparison against a missing/NULL operand is <c>null</c>
/// ("unknown"), and AND/OR/NOT propagate it per Kleene rules — exactly what SQLite's
/// <c>WHERE</c> does. A row matches only when the root evaluates to <c>true</c>, so the
/// predicate backend and the SQL backend never diverge on NULL handling (e.g.
/// <c>not Source = 'x'</c> excludes rows where <c>Source</c> is NULL, in both backends).
/// </remarks>
internal abstract class FilterNode
{
    public abstract void ToSql(SqlBuilder b);
    public abstract bool? Evaluate(LogEvent e);
}

internal sealed class AndNode(FilterNode left, FilterNode right) : FilterNode
{
    public override void ToSql(SqlBuilder b)
    {
        b.Append("(");
        left.ToSql(b);
        b.Append(" AND ");
        right.ToSql(b);
        b.Append(")");
    }

    public override bool? Evaluate(LogEvent e)
    {
        var a = left.Evaluate(e);
        if (a == false)
        {
            return false; // short-circuit: false AND anything = false
        }
        var r = right.Evaluate(e);
        if (r == false)
        {
            return false;
        }
        return a == true && r == true ? true : null;
    }
}

internal sealed class OrNode(FilterNode left, FilterNode right) : FilterNode
{
    public override void ToSql(SqlBuilder b)
    {
        b.Append("(");
        left.ToSql(b);
        b.Append(" OR ");
        right.ToSql(b);
        b.Append(")");
    }

    public override bool? Evaluate(LogEvent e)
    {
        var a = left.Evaluate(e);
        if (a == true)
        {
            return true; // short-circuit: true OR anything = true
        }
        var r = right.Evaluate(e);
        if (r == true)
        {
            return true;
        }
        return a == false && r == false ? false : null;
    }
}

internal sealed class NotNode(FilterNode inner) : FilterNode
{
    public override void ToSql(SqlBuilder b)
    {
        b.Append("(NOT ");
        inner.ToSql(b);
        b.Append(")");
    }

    public override bool? Evaluate(LogEvent e)
    {
        var v = inner.Evaluate(e);
        return v is null ? null : !v;
    }
}

/// <summary>A single <c>ident op value</c> (or null test) comparison.</summary>
internal sealed class ComparisonNode(string ident, CompareOp op, FilterValue? value) : FilterNode
{
    /// <summary>
    /// Ordinal collation for SQL Server comparisons. <c>Compile()</c> compares strings with
    /// <see cref="string.CompareOrdinal(string,string)"/>, and SQL Server's default database
    /// collation is case- and accent-insensitive, so without this a filter would match rows in
    /// search that the live-stream predicate rejects.
    /// </summary>
    private const string OrdinalCollation = "COLLATE Latin1_General_BIN2";

    /// <summary>
    /// Case-insensitive collation for SQL Server <c>LIKE</c>, matching SQLite's default
    /// <c>LIKE</c> and the case-insensitive regex <see cref="LikeMatch"/> uses.
    /// </summary>
    private const string LikeCollation = "COLLATE Latin1_General_CI_AS";

    public override void ToSql(SqlBuilder b)
    {
        var col = SqlColumn(ident, b.Dialect);
        switch (op)
        {
            case CompareOp.IsNull:
                b.Append($"{col} IS NULL");
                return;
            case CompareOp.IsNotNull:
                b.Append($"{col} IS NOT NULL");
                return;
        }

        if (b.Dialect == FilterSqlDialect.SqlServer)
        {
            AppendSqlServer(b, col);
            return;
        }

        var v = value!.Value;
        object? param = v.Kind switch
        {
            ValueKind.Number => v.AsNumber,
            ValueKind.Bool => v.AsBool ? 1 : 0,
            _ => v.AsString,
        };
        b.Append($"{col} {OpText(op)} {b.AddParam(param)}");
    }

    /// <summary>
    /// SQL Server has no equivalent of SQLite's "compare whatever type is in the cell" rule,
    /// so each comparison states the type it wants. <c>JSON_VALUE</c> always yields nvarchar,
    /// and so do all the built-in columns except <c>level</c>; the emitted expression converts
    /// explicitly to whatever the right-hand literal is, which is also what <c>Compile()</c>
    /// does in memory.
    /// </summary>
    private void AppendSqlServer(SqlBuilder b, string col)
    {
        var v = value!.Value;
        var isNumericColumn = IsNumericColumn(ident);

        if (op == CompareOp.Like)
        {
            var text = isNumericColumn ? $"CAST({col} AS NVARCHAR(4000))" : col;
            b.Append($"{text} {LikeCollation} LIKE {b.AddParam(EscapeLikePattern(v.AsString))}");
            return;
        }

        if (v.Kind == ValueKind.Number)
        {
            if (isNumericColumn)
            {
                b.Append($"{col} {OpText(op)} {b.AddParam(v.AsNumber)}");
                return;
            }

            // A text cell compared against a number. TRY_CAST turns "not a number" into NULL,
            // which makes every operator unknown — matching Compile(), which treats a type
            // mismatch as no-match. The one exception is "!=", which Compile() reports as TRUE
            // on a mismatch (1042 really is different from 'eu'), so say that explicitly.
            var number = $"TRY_CAST({col} AS FLOAT)";
            var p = b.AddParam(v.AsNumber);
            b.Append(op == CompareOp.Neq
                ? $"({number} != {p} OR ({col} IS NOT NULL AND {number} IS NULL))"
                : $"{number} {OpText(op)} {p}");
            return;
        }

        // Strings and bools both compare as text: JSON_VALUE renders a JSON true as 'true',
        // and Compile() compares bools through their invariant string form too.
        var lhs = isNumericColumn ? $"CAST({col} AS NVARCHAR(4000))" : col;
        var literal = v.Kind == ValueKind.Bool ? (v.AsBool ? "true" : "false") : v.AsString;
        b.Append($"{lhs} {OrdinalCollation} {OpText(op)} {b.AddParam(literal)}");
    }

    private static string OpText(CompareOp op) => op switch
    {
        CompareOp.Eq => "=",
        CompareOp.Neq => "!=",
        CompareOp.Gt => ">",
        CompareOp.Gte => ">=",
        CompareOp.Lt => "<",
        CompareOp.Lte => "<=",
        CompareOp.Like => "LIKE",
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// SQL Server's <c>LIKE</c> reads <c>[...]</c> as a character class; SQLite's does not, and
    /// neither does <see cref="LikeMatch"/>. Neutralise it so the same pattern means the same
    /// thing on both backends. <c>%</c> and <c>_</c> are wildcards everywhere and stay as-is.
    /// </summary>
    private static string EscapeLikePattern(string pattern) => pattern.Replace("[", "[[]");

    public override bool? Evaluate(LogEvent e)
    {
        var actual = e.Resolve(ident);
        switch (op)
        {
            case CompareOp.IsNull:
                return actual is null;
            case CompareOp.IsNotNull:
                return actual is not null;
            default:
                // A comparison with a NULL operand is "unknown" (SQL NULL), not false.
                return actual is null ? null : Compare(actual, op, value!.Value);
        }
    }

    private static string SqlColumn(string ident, FilterSqlDialect dialect)
    {
        if (FilterIdent.IsProperty(ident))
        {
            return JsonAccess(FilterIdent.PropertyName(ident), dialect);
        }
        return ident switch
        {
            "Level" => "level",
            "Message" => "message",
            "Exception" => "exception",
            "Source" => "source",
            "TraceId" => "trace_id",
            "SpanId" => "span_id",
            "Machine" => "machine",
            _ => JsonAccess(ident, dialect),
        };
    }

    private static string JsonAccess(string property, FilterSqlDialect dialect) =>
        dialect == FilterSqlDialect.SqlServer
            ? $"JSON_VALUE(properties, '$.{property}')"
            : $"json_extract(properties, '$.{property}')";

    /// <summary>
    /// Whether the identifier's SQL expression is numeric. Only <c>level</c> is; every other
    /// built-in column is text, and a property read out of JSON is text on SQL Server whatever
    /// the JSON type was.
    /// </summary>
    private static bool IsNumericColumn(string ident) =>
        !FilterIdent.IsProperty(ident) && ident == "Level";

    private static bool Compare(object actual, CompareOp op, FilterValue expected)
    {
        if (op == CompareOp.Like)
        {
            return LikeMatch(PropertyValue.ToInvariantString(actual), expected.AsString);
        }

        switch (expected.Kind)
        {
            case ValueKind.Number:
                if (TryToNumber(actual, out var an))
                {
                    return NumberCompare(an, op, expected.AsNumber);
                }
                return op == CompareOp.Neq; // type mismatch: only "!=" is true
            case ValueKind.Bool:
                if (actual is bool ab)
                {
                    var eq = ab == expected.AsBool;
                    return op == CompareOp.Eq ? eq : op == CompareOp.Neq ? !eq : false;
                }
                return op == CompareOp.Neq;
            default: // String
                var cmp = string.CompareOrdinal(PropertyValue.ToInvariantString(actual), expected.AsString);
                return StringCompare(cmp, op);
        }
    }

    private static bool TryToNumber(object? value, out double n)
    {
        switch (value)
        {
            case long l: n = l; return true;
            case int i: n = i; return true;
            case double d: n = d; return true;
            case float f: n = f; return true;
            default: n = 0; return false;
        }
    }

    private static bool NumberCompare(double a, CompareOp op, double b) => op switch
    {
        CompareOp.Eq => a == b,
        CompareOp.Neq => a != b,
        CompareOp.Gt => a > b,
        CompareOp.Gte => a >= b,
        CompareOp.Lt => a < b,
        CompareOp.Lte => a <= b,
        _ => false,
    };

    private static bool StringCompare(int cmp, CompareOp op) => op switch
    {
        CompareOp.Eq => cmp == 0,
        CompareOp.Neq => cmp != 0,
        CompareOp.Gt => cmp > 0,
        CompareOp.Gte => cmp >= 0,
        CompareOp.Lt => cmp < 0,
        CompareOp.Lte => cmp <= 0,
        _ => false,
    };

    /// <summary>
    /// Mirrors SQLite's default LIKE: <c>%</c> matches any run, <c>_</c> any single char,
    /// case-insensitive for ASCII.
    /// </summary>
    private static bool LikeMatch(string input, string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '%' => ".*",
                '_' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return Regex.IsMatch(input, sb.ToString(),
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
