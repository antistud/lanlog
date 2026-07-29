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
internal sealed class SqlBuilder
{
    private readonly StringBuilder _sql = new();
    private readonly List<object?> _params = [];

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
    public override void ToSql(SqlBuilder b)
    {
        var col = SqlColumn(ident);
        switch (op)
        {
            case CompareOp.IsNull:
                b.Append($"{col} IS NULL");
                return;
            case CompareOp.IsNotNull:
                b.Append($"{col} IS NOT NULL");
                return;
        }

        var v = value!.Value;
        var opText = op switch
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

        object? param = v.Kind switch
        {
            ValueKind.Number => v.AsNumber,
            ValueKind.Bool => v.AsBool ? 1 : 0,
            _ => v.AsString,
        };
        b.Append($"{col} {opText} {b.AddParam(param)}");
    }

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

    private static string SqlColumn(string ident)
    {
        if (FilterIdent.IsProperty(ident))
        {
            return $"json_extract(properties, '$.{FilterIdent.PropertyName(ident)}')";
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
            _ => $"json_extract(properties, '$.{ident}')",
        };
    }

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
