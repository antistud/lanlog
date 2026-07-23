using Logrr.Contracts;
using Logrr.Core;
using Logrr.Core.Filters;
using Xunit;

namespace Logrr.Tests;

public class FilterExpressionTests
{
    private static LogEvent Event(
        LogLevel level = LogLevel.Information,
        string message = "hello",
        string? exception = null,
        string? source = null,
        params (string Key, object? Value)[] props)
    {
        var bag = props.ToDictionary(p => p.Key, p => p.Value);
        return new LogEvent
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Level = level,
            Message = message,
            Exception = exception,
            Source = source,
            Properties = bag,
        };
    }

    [Theory]
    [InlineData("Level >= Warning")]
    [InlineData("UserId = 1042")]
    [InlineData("Message like '%timeout%' and not Source = 'HealthCheck'")]
    [InlineData("Exception is not null")]
    [InlineData("(Level >= Warning and TenantId = 42) or UserId = 1042")]
    public void Valid_expressions_parse(string expr)
    {
        Assert.True(FilterExpression.TryParse(expr, out _, out var err), err);
    }

    [Theory]
    [InlineData("Level >=")]                 // missing value
    [InlineData("= 5")]                       // missing ident
    [InlineData("Source = HealthCheck")]      // unquoted string that isn't a level
    [InlineData("Level >> 3")]                // unknown operator
    [InlineData("(Level = 3")]                // unbalanced paren
    [InlineData("")]                           // empty
    public void Invalid_expressions_are_rejected(string expr)
    {
        Assert.False(FilterExpression.TryParse(expr, out _, out _));
    }

    [Fact]
    public void Level_comparison_uses_ordering()
    {
        var f = FilterExpression.Parse("Level >= Warning");
        Assert.True(f.Evaluate(Event(LogLevel.Error)));
        Assert.False(f.Evaluate(Event(LogLevel.Information)));
    }

    [Fact]
    public void Not_over_null_source_excludes_the_row()
    {
        // Three-valued logic: NOT (NULL = 'x') is NULL, not TRUE — the row is excluded,
        // matching SQLite's WHERE. (Regression guard for SQL/predicate agreement.)
        var f = FilterExpression.Parse("not Source = 'HealthCheck'");
        Assert.False(f.Evaluate(Event(source: null)));
        Assert.True(f.Evaluate(Event(source: "Billing")));
        Assert.False(f.Evaluate(Event(source: "HealthCheck")));
    }

    [Fact]
    public void Like_is_case_insensitive_like_sqlite()
    {
        var f = FilterExpression.Parse("Message like '%TIMEOUT%'");
        Assert.True(f.Evaluate(Event(message: "a timeout occurred")));
    }

    [Fact]
    public void Property_ident_injection_is_rejected()
    {
        Assert.False(FilterExpression.TryParse("UserId')='1' or '1'=('1 = 1", out _, out _));
    }
}
