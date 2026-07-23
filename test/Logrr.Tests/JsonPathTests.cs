using Logrr.Notify;
using Xunit;

namespace Logrr.Tests;

public class JsonPathTests
{
    [Theory]
    [InlineData("""{"number":42,"html_url":"http://x/42"}""", "$.number", "42")]
    [InlineData("""{"number":42,"html_url":"http://x/42"}""", "$.html_url", "http://x/42")]
    [InlineData("""{"ticket":{"id":7,"url":"http://z/7"}}""", "$.ticket.id", "7")]
    [InlineData("""{"ticket":{"id":7,"url":"http://z/7"}}""", "$.ticket.url", "http://z/7")]
    [InlineData("""{"items":[{"id":1},{"id":2}]}""", "$.items[1].id", "2")]
    public void Extracts_values(string json, string path, string expected)
    {
        Assert.Equal(expected, JsonPath.Extract(json, path));
    }

    [Theory]
    [InlineData("""{"a":1}""", "$.missing")]
    [InlineData("not json", "$.a")]
    [InlineData("""{"a":1}""", null)]
    public void Returns_null_for_missing_or_bad(string json, string? path)
    {
        Assert.Null(JsonPath.Extract(json, path));
    }
}
