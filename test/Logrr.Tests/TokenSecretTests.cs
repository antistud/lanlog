using Logrr.Contracts;
using Logrr.Server.Auth;
using Logrr.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Logrr.Storage;
using Logrr.Storage.Control;
using Xunit;

namespace Logrr.Tests;

public class TokenSecretTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-tok-" + Guid.NewGuid().ToString("N"));
    private readonly ControlDatabase _db;
    private readonly TokenStore _tokens;
    private readonly DateTimeOffset _now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    public TokenSecretTests()
    {
        _db = new ControlDatabase(new StoragePaths(_root));
        _db.Initialize();
        new AppStore(_db).Create(new AppRecord
        {
            Id = "lanticket", Name = "LanTicket", RetentionDays = 14, MaxSizeMb = 2048,
            MinimumLevel = LogLevel.Verbose, IndexedProperties = [], IsEnabled = true, CreatedUtc = _now,
        });
        _tokens = new TokenStore(_db);
    }

    // Every token of an app must land on its own prefix row; the column is UNIQUE.
    [Fact]
    public void Two_tokens_for_the_same_app_can_coexist()
    {
        var a = Issue("lanticket");
        var b = Issue("lanticket");

        Assert.NotEqual(TokenSecret.Prefix(a), TokenSecret.Prefix(b));
        Assert.Equal(2, _tokens.ListByApp("lanticket").Count);
    }

    // The prefix is the lookup key on the ingest hot path: it must round-trip from the secret.
    [Fact]
    public void Prefix_resolves_the_issuing_token()
    {
        var a = Issue("lanticket");
        var b = Issue("lanticket");

        Assert.True(TokenSecret.Verify(a, _tokens.FindByPrefix(TokenSecret.Prefix(a))!.Hash));
        Assert.True(TokenSecret.Verify(b, _tokens.FindByPrefix(TokenSecret.Prefix(b))!.Hash));
    }

    // Tokens issued under the old head-only prefix are already in the field; the new lookup
    // must not lock them out.
    [Fact]
    public void Legacy_prefix_tokens_still_authenticate()
    {
        var secret = TokenSecret.Generate("lanticket");
        _tokens.Create(new TokenRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            AppId = "lanticket",
            Prefix = TokenSecret.LegacyPrefix(secret), // the retired scheme
            Hash = TokenSecret.Hash(secret),
            Scopes = TokenScopes.Ingest,
            CreatedUtc = _now,
        });

        Assert.True(Authenticate(secret, TokenScopes.Ingest).Ok);
        Assert.False(Authenticate(TokenSecret.Generate("lanticket"), TokenScopes.Ingest).Ok);
    }

    private AuthResult Authenticate(string secret, TokenScopes required)
    {
        var auth = new TokenAuthenticator(_tokens, new AppStore(_db),
            new MemoryCache(new MemoryCacheOptions()));
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Logrr-ApiKey"] = secret;
        return auth.Authenticate(http, required);
    }

    private string Issue(string appId)
    {
        var secret = TokenSecret.Generate(appId);
        _tokens.Create(new TokenRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            AppId = appId,
            Prefix = TokenSecret.Prefix(secret),
            Hash = TokenSecret.Hash(secret),
            Scopes = TokenScopes.Ingest | TokenScopes.Read,
            CreatedUtc = _now,
        });
        return secret;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
