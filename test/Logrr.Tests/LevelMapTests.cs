using Logrr.Contracts;
using Logrr.Core;
using Xunit;

namespace Logrr.Tests;

public class LevelMapTests
{
    [Theory]
    [InlineData("Verbose", LogLevel.Verbose)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Fatal", LogLevel.Fatal)]
    [InlineData("Trace", LogLevel.Verbose)]   // MEL alias
    [InlineData("Warn", LogLevel.Warning)]     // MEL alias
    [InlineData("Critical", LogLevel.Fatal)]   // MEL alias
    [InlineData("3", LogLevel.Warning)]        // numeric
    public void Parses_known_levels(string input, LogLevel expected)
    {
        Assert.True(LevelMap.TryParse(input, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Loud")]
    [InlineData("9")]
    public void Unknown_levels_fall_back_to_information(string? input)
    {
        Assert.False(LevelMap.TryParse(input, out var level));
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal(LogLevel.Information, LevelMap.ParseOrDefault(input));
    }
}
