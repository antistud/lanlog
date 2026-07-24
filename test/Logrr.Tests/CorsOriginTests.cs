using Logrr.Server.Ingest;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Logrr.Tests;

public class CorsOriginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-cors-" + Guid.NewGuid().ToString("N"));
    private readonly ControlDatabase _db;
    private readonly CorsOriginStore _store;
    private DateTimeOffset _now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    public CorsOriginTests()
    {
        _db = new ControlDatabase(new StoragePaths(_root));
        _db.Initialize();
        _store = new CorsOriginStore(_db);
    }

    private IngestCorsPolicy Policy(params string[] configOrigins)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configOrigins.Select((o, i) =>
                new KeyValuePair<string, string?>($"Ingest:AllowedOrigins:{i}", o)))
            .Build();
        return new IngestCorsPolicy(_store, config, () => _now);
    }

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("https://App.Example.com/", "https://app.example.com")]        // lower-cased, trailing slash dropped
    [InlineData("https://app.example.com/some/path", "https://app.example.com")] // path stripped
    [InlineData("http://localhost:5173", "http://localhost:5173")]              // port kept
    [InlineData("https://app.example.com:443", "https://app.example.com")]      // default port omitted
    public void Normalize_reduces_input_to_an_origin(string input, string expected)
    {
        Assert.Equal(expected, CorsOriginStore.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("app.example.com")]      // no scheme
    [InlineData("ftp://example.com")]    // not http(s)
    public void Normalize_rejects_non_origins(string input)
    {
        Assert.Null(CorsOriginStore.Normalize(input));
    }

    [Fact]
    public void Empty_allowlist_allows_any_origin()
    {
        var policy = Policy();
        Assert.True(policy.AllowAny);
        Assert.True(policy.IsAllowed("https://anything.example"));
    }

    [Fact]
    public void Added_origin_is_allowed_and_others_are_not()
    {
        var policy = Policy();
        var normalized = policy.Add("https://App.Example.com/");
        Assert.Equal("https://app.example.com", normalized);

        Assert.False(policy.AllowAny);
        Assert.True(policy.IsAllowed("https://app.example.com"));
        Assert.False(policy.IsAllowed("https://evil.example"));
    }

    [Fact]
    public void Invalid_origin_is_not_added()
    {
        var policy = Policy();
        Assert.Null(policy.Add("nonsense"));
        Assert.True(policy.AllowAny); // nothing was stored
    }

    [Fact]
    public void Config_origins_union_with_ui_managed_ones()
    {
        var policy = Policy("https://config.example");
        Assert.False(policy.AllowAny);
        Assert.True(policy.IsAllowed("https://config.example"));

        policy.Add("https://ui.example");
        Assert.True(policy.IsAllowed("https://config.example"));
        Assert.True(policy.IsAllowed("https://ui.example"));

        // Config origins are surfaced separately from UI-managed ones.
        Assert.Contains("https://config.example", policy.ConfigOrigins);
        Assert.DoesNotContain(policy.Managed(), o => o.Origin == "https://config.example");
        Assert.Contains(policy.Managed(), o => o.Origin == "https://ui.example");
    }

    [Fact]
    public void Removed_origin_stops_being_allowed()
    {
        var policy = Policy();
        policy.Add("https://app.example.com");
        policy.Remove("https://app.example.com");
        Assert.True(policy.AllowAny);
        Assert.Empty(policy.Managed());
    }

    [Fact]
    public void Add_is_idempotent()
    {
        Assert.True(_store.Add("https://a.example", _now));
        Assert.False(_store.Add("https://a.example", _now)); // already present
        Assert.Single(_store.List());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
