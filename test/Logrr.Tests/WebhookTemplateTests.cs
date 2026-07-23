using System.Text.Json;
using Logrr.Core;
using Xunit;

namespace Logrr.Tests;

public class WebhookTemplateTests
{
    private static Func<string, string?> Resolver(Dictionary<string, string?> map) =>
        path => map.TryGetValue(path, out var v) ? v : null;

    [Fact]
    public void Substitutes_tokens_outside_strings_unescaped()
    {
        var map = new Dictionary<string, string?> { ["occurrence.count"] = "4102" };
        var result = WebhookTemplate.Render(
            "{ \"count\": {{occurrence.count}} }", Resolver(map), jsonMode: true);
        Assert.Equal("{ \"count\": 4102 }", result);
    }

    [Fact]
    public void Escapes_tokens_inside_json_string_literals()
    {
        // A stack trace full of quotes and newlines must not break the JSON payload.
        var nasty = "System.Exception: \"boom\"\n  at Foo()";
        var map = new Dictionary<string, string?> { ["event.exception"] = nasty };
        var rendered = WebhookTemplate.Render(
            "{ \"body\": \"{{event.exception}}\" }", Resolver(map), jsonMode: true);

        // The rendered body must parse as valid JSON and preserve the value.
        using var doc = JsonDocument.Parse(rendered);
        Assert.Equal(nasty, doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void Truncate_filter_limits_length()
    {
        var map = new Dictionary<string, string?> { ["event.message"] = new string('x', 500) };
        var rendered = WebhookTemplate.Render("{{event.message | truncate:10}}", Resolver(map), jsonMode: false);
        Assert.Equal(new string('x', 10) + "…", rendered);
    }

    [Fact]
    public void Default_filter_fills_empty_values()
    {
        var map = new Dictionary<string, string?> { ["user.name"] = "" };
        var rendered = WebhookTemplate.Render("{{user.name | default:\"n/a\"}}", Resolver(map), jsonMode: false);
        Assert.Equal("n/a", rendered);
    }

    [Fact]
    public void Upper_filter_uppercases()
    {
        var map = new Dictionary<string, string?> { ["event.level"] = "error" };
        var rendered = WebhookTemplate.Render("{{event.level | upper}}", Resolver(map), jsonMode: false);
        Assert.Equal("ERROR", rendered);
    }

    [Fact]
    public void Md_filter_wraps_in_code_fence()
    {
        var map = new Dictionary<string, string?> { ["event.exception"] = "boom" };
        var rendered = WebhookTemplate.Render("{{event.exception | md}}", Resolver(map), jsonMode: false);
        Assert.Equal("```\nboom\n```", rendered);
    }

    [Fact]
    public void Unknown_paths_render_empty()
    {
        var rendered = WebhookTemplate.Render("[{{does.not.exist}}]", Resolver([]), jsonMode: false);
        Assert.Equal("[]", rendered);
    }
}
