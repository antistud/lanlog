using System.Text.Json;
using Logrr.Contracts;
using Logrr.Core;
using Xunit;

namespace Logrr.Tests;

public class ClefParserTests
{
    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly DateTimeOffset Now = new(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_a_seq_style_line()
    {
        var line = """
        {"@t":"2026-07-23T14:02:11.4270000Z","@mt":"Payment {Amount} failed for {UserId}","@l":"Error","@x":"System.TimeoutException: x","Amount":49.99,"UserId":1042,"SourceContext":"Billing.Processor"}
        """;
        var r = ClefParser.Parse(Obj(line), Now);

        Assert.True(r.Ok);
        var e = r.Event!;
        Assert.Equal(LogLevel.Error, e.Level);
        Assert.Equal("Payment {Amount} failed for {UserId}", e.Template);
        Assert.Equal("Payment 49.99 failed for 1042", e.Message); // @m absent → rendered
        Assert.Equal("System.TimeoutException: x", e.Exception);
        Assert.Equal("Billing.Processor", e.Source);
        Assert.Equal(1042L, e.Properties["UserId"]);
    }

    [Fact]
    public void Uses_rendered_message_when_present()
    {
        var line = """{"@t":"2026-07-23T14:02:11Z","@mt":"Hi {Name}","@m":"Hi Bob","Name":"Bob"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.Equal("Hi Bob", r.Event!.Message);
    }

    [Fact]
    public void Absent_level_is_information()
    {
        var line = """{"@t":"2026-07-23T14:02:11Z","@mt":"tick"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.Equal(LogLevel.Information, r.Event!.Level);
    }

    [Fact]
    public void Unrecognised_level_preserved_as_rawLevel()
    {
        var line = """{"@t":"2026-07-23T14:02:11Z","@mt":"x","@l":"Loud"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.Equal(LogLevel.Information, r.Event!.Level);
        Assert.Equal("Loud", r.Event!.Properties["_rawLevel"]);
    }

    [Fact]
    public void Double_at_escapes_to_literal_property()
    {
        var line = """{"@t":"2026-07-23T14:02:11Z","@mt":"x","@@x":"literal"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.Equal("literal", r.Event!.Properties["@x"]);
    }

    [Fact]
    public void Invalid_timestamp_is_a_line_error_not_an_exception()
    {
        var line = """{"@t":"not-a-date","@mt":"x"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.False(r.Ok);
        Assert.Contains("@t", r.Error);
    }

    [Fact]
    public void Absent_timestamp_uses_server_clock()
    {
        var line = """{"@mt":"x"}""";
        var r = ClefParser.Parse(Obj(line), Now);
        Assert.True(r.Ok);
        Assert.Equal(Now, r.Event!.Timestamp);
    }
}
