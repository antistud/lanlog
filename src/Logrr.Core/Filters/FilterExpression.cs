using System.Text.RegularExpressions;

namespace Logrr.Core.Filters;

/// <summary>
/// A parsed filter expression (SPEC §7.1). One parser, two backends: <see cref="ToSql"/>
/// for historical queries and <see cref="Compile"/> for realtime subscriptions and rule
/// evaluation. The backends are property-tested against each other so a rule that fires in
/// the live stream is always reproducible by the same filter in search.
/// </summary>
public sealed partial class FilterExpression
{
    private readonly FilterNode _root;

    private FilterExpression(FilterNode root) => _root = root;

    /// <summary>Parse or throw <see cref="FilterParseException"/>.</summary>
    public static FilterExpression Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FilterParseException("empty expression", 0);
        }

        var tokens = new FilterLexer(text).Tokenize();
        var parser = new Parser(tokens);
        var root = parser.ParseExpr();
        parser.Expect(TokenKind.End, "unexpected trailing input");
        return new FilterExpression(root);
    }

    /// <summary>Parse without throwing; returns false and an error message on failure.</summary>
    public static bool TryParse(string text, out FilterExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (FilterParseException ex)
        {
            expression = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Emit a parameterised SQL <c>WHERE</c> fragment for the query backend.</summary>
    public (string Sql, IReadOnlyList<object?> Parameters) ToSql()
    {
        var b = new SqlBuilder();
        _root.ToSql(b);
        return (b.Sql, b.Parameters);
    }

    /// <summary>
    /// Compile to an in-memory predicate for subscriptions and rules. A row matches only
    /// when the expression evaluates to <c>true</c> — an "unknown" (NULL) result is not a
    /// match, mirroring SQLite's <c>WHERE</c>.
    /// </summary>
    public Func<LogEvent, bool> Compile() => e => _root.Evaluate(e) == true;

    /// <summary>Evaluate the predicate directly.</summary>
    public bool Evaluate(LogEvent e) => _root.Evaluate(e) == true;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PropertyIdentRegex();

    internal static void ValidateIdent(string ident, int position)
    {
        if (FilterIdent.IsBuiltIn(ident))
        {
            return;
        }
        // A "Properties."-qualified identifier is validated on the name it resolves to, so
        // the qualifier can never widen what a property name is allowed to contain.
        if (!PropertyIdentRegex().IsMatch(FilterIdent.PropertyName(ident)))
        {
            throw new FilterParseException($"invalid identifier '{ident}'", position);
        }
    }

    /// <summary>Recursive-descent parser following the grammar in SPEC §7.1.</summary>
    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        private Token Current => tokens[_pos];

        private Token Advance() => tokens[_pos++];

        public void Expect(TokenKind kind, string message)
        {
            if (Current.Kind != kind)
            {
                throw new FilterParseException(message, Current.Position);
            }
            _pos++;
        }

        // expr := term (('and' | 'or') term)*
        public FilterNode ParseExpr()
        {
            var left = ParseTerm();
            while (Current.Kind is TokenKind.And or TokenKind.Or)
            {
                var isAnd = Advance().Kind == TokenKind.And;
                var right = ParseTerm();
                left = isAnd ? new AndNode(left, right) : new OrNode(left, right);
            }
            return left;
        }

        // term := '(' expr ')' | 'not' term | comparison
        private FilterNode ParseTerm()
        {
            switch (Current.Kind)
            {
                case TokenKind.LParen:
                    Advance();
                    var inner = ParseExpr();
                    Expect(TokenKind.RParen, "expected ')'");
                    return inner;
                case TokenKind.Not:
                    Advance();
                    return new NotNode(ParseTerm());
                default:
                    return ParseComparison();
            }
        }

        // comparison := ident op value | ident 'like' string | ident 'is' ['not'] 'null'
        private FilterNode ParseComparison()
        {
            if (Current.Kind != TokenKind.Ident)
            {
                throw new FilterParseException("expected an identifier", Current.Position);
            }
            var identTok = Advance();
            var ident = identTok.Text;
            ValidateIdent(ident, identTok.Position);

            switch (Current.Kind)
            {
                case TokenKind.Is:
                    Advance();
                    if (Current.Kind == TokenKind.Not)
                    {
                        Advance();
                        Expect(TokenKind.Null, "expected 'null' after 'is not'");
                        return new ComparisonNode(ident, CompareOp.IsNotNull, null);
                    }
                    Expect(TokenKind.Null, "expected 'null' after 'is'");
                    return new ComparisonNode(ident, CompareOp.IsNull, null);

                case TokenKind.Like:
                    Advance();
                    if (Current.Kind != TokenKind.String)
                    {
                        throw new FilterParseException("'like' requires a string pattern", Current.Position);
                    }
                    var pat = (string)Advance().Value!;
                    return new ComparisonNode(ident, CompareOp.Like, new FilterValue(ValueKind.String, pat));

                case TokenKind.Op:
                    var op = MapOp(Advance());
                    var value = ParseValue();
                    return new ComparisonNode(ident, op, value);

                default:
                    throw new FilterParseException("expected an operator", Current.Position);
            }
        }

        private FilterValue ParseValue()
        {
            var t = Current;
            switch (t.Kind)
            {
                case TokenKind.String:
                    Advance();
                    return new FilterValue(ValueKind.String, (string)t.Value!);
                case TokenKind.Number:
                    Advance();
                    return new FilterValue(ValueKind.Number, (double)t.Value!);
                case TokenKind.True:
                    Advance();
                    return new FilterValue(ValueKind.Bool, true);
                case TokenKind.False:
                    Advance();
                    return new FilterValue(ValueKind.Bool, false);
                case TokenKind.Ident:
                    // A bare word as a value must be a level name (SPEC §7.1 value := ... | level-name).
                    if (LevelMap.TryParse(t.Text, out var level))
                    {
                        Advance();
                        return new FilterValue(ValueKind.Number, (double)(int)level);
                    }
                    throw new FilterParseException($"unexpected value '{t.Text}' (quote strings with '')", t.Position);
                default:
                    throw new FilterParseException("expected a value", t.Position);
            }
        }

        private static CompareOp MapOp(Token t) => t.Text switch
        {
            "=" => CompareOp.Eq,
            "!=" => CompareOp.Neq,
            ">" => CompareOp.Gt,
            ">=" => CompareOp.Gte,
            "<" => CompareOp.Lt,
            "<=" => CompareOp.Lte,
            _ => throw new FilterParseException($"unknown operator '{t.Text}'", t.Position),
        };
    }
}
