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
