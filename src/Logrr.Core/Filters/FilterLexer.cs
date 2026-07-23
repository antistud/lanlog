using System.Globalization;

namespace Logrr.Core.Filters;

internal enum TokenKind
{
    Ident,
    String,
    Number,
    LParen,
    RParen,
    Op,        // = != > >= < <=
    And,
    Or,
    Not,
    Like,
    Is,
    Null,
    True,
    False,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position, object? Value = null);

/// <summary>
/// Tokenises a filter expression. One lexer feeds the single parser whose AST drives both
/// compilation targets (SPEC §7.1).
/// </summary>
internal sealed class FilterLexer(string source)
{
    private readonly string _s = source ?? string.Empty;
    private int _i;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var t = Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.End)
            {
                break;
            }
        }
        return tokens;
    }

    private Token Next()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i]))
        {
            _i++;
        }

        if (_i >= _s.Length)
        {
            return new Token(TokenKind.End, string.Empty, _i);
        }

        var start = _i;
        var c = _s[_i];

        switch (c)
        {
            case '(':
                _i++;
                return new Token(TokenKind.LParen, "(", start);
            case ')':
                _i++;
                return new Token(TokenKind.RParen, ")", start);
            case '\'':
                return ReadString(start);
            case '=':
                _i++;
                return new Token(TokenKind.Op, "=", start);
            case '!':
                if (Peek(1) == '=')
                {
                    _i += 2;
                    return new Token(TokenKind.Op, "!=", start);
                }
                throw new FilterParseException("unexpected '!'", start);
            case '>':
            case '<':
                if (Peek(1) == '=')
                {
                    _i += 2;
                    return new Token(TokenKind.Op, $"{c}=", start);
                }
                _i++;
                return new Token(TokenKind.Op, c.ToString(), start);
        }

        if (char.IsDigit(c) || (c == '-' && char.IsDigit(Peek(1))))
        {
            return ReadNumber(start);
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ReadWord(start);
        }

        throw new FilterParseException($"unexpected character '{c}'", start);
    }

    private char Peek(int ahead) => _i + ahead < _s.Length ? _s[_i + ahead] : '\0';

    private Token ReadString(int start)
    {
        // Opening quote at _i. SQL-style '' escapes an embedded quote.
        _i++; // consume opening '
        var sb = new System.Text.StringBuilder();
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == '\'')
            {
                if (Peek(1) == '\'')
                {
                    sb.Append('\'');
                    _i += 2;
                    continue;
                }
                _i++; // consume closing '
                return new Token(TokenKind.String, sb.ToString(), start, sb.ToString());
            }
            sb.Append(c);
            _i++;
        }
        throw new FilterParseException("unterminated string literal", start);
    }

    private Token ReadNumber(int start)
    {
        _i++; // first digit or '-'
        while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.'))
        {
            _i++;
        }
        var text = _s[start.._i];
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            throw new FilterParseException($"invalid number '{text}'", start);
        }
        return new Token(TokenKind.Number, text, start, d);
    }

    private Token ReadWord(int start)
    {
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.'))
        {
            _i++;
        }
        var text = _s[start.._i];
        return text.ToLowerInvariant() switch
        {
            "and" => new Token(TokenKind.And, text, start),
            "or" => new Token(TokenKind.Or, text, start),
            "not" => new Token(TokenKind.Not, text, start),
            "like" => new Token(TokenKind.Like, text, start),
            "is" => new Token(TokenKind.Is, text, start),
            "null" => new Token(TokenKind.Null, text, start),
            "true" => new Token(TokenKind.True, text, start),
            "false" => new Token(TokenKind.False, text, start),
            _ => new Token(TokenKind.Ident, text, start),
        };
    }
}
