using Logrr.Core;
using Xunit;

namespace Logrr.Tests;

public class MessageTemplateTests
{
    [Fact]
    public void Renders_named_holes()
    {
        var props = new Dictionary<string, object?> { ["Amount"] = 49.99, ["UserId"] = 1042L };
        Assert.Equal("Payment 49.99 failed for 1042",
            MessageTemplate.Render("Payment {Amount} failed for {UserId}", props));
    }

    [Fact]
    public void Tokenize_splits_literals_and_holes()
    {
        var segs = MessageTemplate.Tokenize("Payment {Amount} failed for {@UserId}");
        Assert.Equal(4, segs.Count);
        Assert.False(segs[0].IsHole);
        Assert.Equal("Payment ", segs[0].Text);
        Assert.True(segs[1].IsHole);
        Assert.Equal("Amount", segs[1].Name);
        Assert.False(segs[2].IsHole);
        Assert.Equal(" failed for ", segs[2].Text);
        Assert.True(segs[3].IsHole);
        Assert.Equal("UserId", segs[3].Name); // destructuring hint stripped
    }

    [Fact]
    public void Tokenize_unescapes_braces_and_strips_format()
    {
        var segs = MessageTemplate.Tokenize("{{literal}} {Count:N0} done");
        Assert.Equal("{literal} ", segs[0].Text);
        Assert.True(segs[1].IsHole);
        Assert.Equal("Count", segs[1].Name); // format specifier dropped
        Assert.Equal(" done", segs[2].Text);
    }

    [Fact]
    public void Honours_destructuring_hints()
    {
        var props = new Dictionary<string, object?> { ["Order"] = "A1" };
        Assert.Equal("Got A1", MessageTemplate.Render("Got {@Order}", props));
    }

    [Fact]
    public void Escapes_double_braces()
    {
        Assert.Equal("{literal}", MessageTemplate.Render("{{literal}}", new Dictionary<string, object?>()));
    }

    [Fact]
    public void Unmatched_hole_renders_its_name()
    {
        Assert.Equal("value is Missing", MessageTemplate.Render("value is {Missing}", new Dictionary<string, object?>()));
    }
}
