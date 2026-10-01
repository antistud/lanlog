using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Logrr.Contracts;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// Windows integrated sign-in (SPEC §11): the identity → account mapping, and the routing that
/// keeps a failed Windows sign-in from locking everyone out of the password form.
/// </summary>
public class WindowsAccountMappingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-winauth-" + Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly DateTimeOffset _now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    public WindowsAccountMappingTests()
    {
        var db = new ControlDatabase(new StoragePaths(_root));
        db.Initialize();
        _users = new UserStore(db);
    }

    private UserRecord Add(string username, string? windowsAccount, byte[]? hash = null) =>
        Save(new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            WindowsAccount = windowsAccount,
            PasswordHash = hash,
            PasswordSalt = hash is null ? null : [1, 2, 3],
            Role = UserRole.User,
            CreatedUtc = _now,
        });

    private UserRecord Save(UserRecord u)
    {
        _users.Create(u);
        return u;
    }

    [Fact]
    public void Lookup_matches_the_windows_identity_case_insensitively()
    {
        Add("jon", @"CONTOSO\jrhoades");

        // The server reports whatever casing the client sent; Windows account names don't care.
        Assert.Equal("jon", _users.GetByWindowsAccount(@"contoso\JRHOADES")?.Username);
        Assert.Equal("jon", _users.GetByWindowsAccount(@"CONTOSO\jrhoades")?.Username);
    }

    [Fact]
    public void An_unmapped_identity_resolves_to_no_account()
    {
        Add("jon", @"CONTOSO\jrhoades");
        Add("sam", windowsAccount: null, hash: [9, 9]);

        Assert.Null(_users.GetByWindowsAccount(@"CONTOSO\someone-else"));
        Assert.Null(_users.GetByWindowsAccount(""));
        Assert.Null(_users.GetByWindowsAccount("   "));
    }

    [Fact]
    public void A_windows_only_account_has_no_password_to_sign_in_with()
    {
        var user = Add("svc", @"CONTOSO\svc-logrr");

        var loaded = _users.GetByWindowsAccount(@"CONTOSO\svc-logrr");
        Assert.Equal(user.Id, loaded!.Id);
        Assert.Null(loaded.PasswordHash); // /auth/login rejects an account with no hash
        Assert.Equal(@"CONTOSO\svc-logrr", loaded.WindowsAccount);
    }

    [Fact]
    public void Two_accounts_cannot_claim_the_same_windows_identity()
    {
        Add("jon", @"CONTOSO\jrhoades");
        var other = Add("jon-admin", windowsAccount: null, hash: [7]);

        Assert.False(_users.SetWindowsAccount(other.Id, @"contoso\JRhoades")); // differs only by case
        Assert.Null(_users.GetByUsername("jon-admin")!.WindowsAccount);
        Assert.Equal("jon", _users.GetByWindowsAccount(@"CONTOSO\jrhoades")?.Username);
    }

    [Fact]
    public void Mapping_can_be_set_moved_and_cleared()
    {
        var jon = Add("jon", windowsAccount: null, hash: [7]);
        var sam = Add("sam", windowsAccount: null, hash: [8]);

        Assert.True(_users.SetWindowsAccount(jon.Id, @"CONTOSO\jrhoades"));
        Assert.Equal("jon", _users.GetByWindowsAccount(@"CONTOSO\jrhoades")?.Username);

        // Re-setting the same account on the same user is not a conflict with itself.
        Assert.True(_users.SetWindowsAccount(jon.Id, @"CONTOSO\jrhoades"));

        // Unlink, then the identity is free for someone else.
        Assert.True(_users.SetWindowsAccount(jon.Id, null));
        Assert.Null(_users.GetByWindowsAccount(@"CONTOSO\jrhoades"));
        Assert.True(_users.SetWindowsAccount(sam.Id, @"CONTOSO\jrhoades"));
        Assert.Equal("sam", _users.GetByWindowsAccount(@"CONTOSO\jrhoades")?.Username);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The <c>/auth/windows</c> route and the sign-in page routing that makes auto sign-in safe to
/// turn on.
///
/// The real Negotiate handler cannot run here: it defers to the host under IIS and does the
/// handshake itself under Kestrel, but TestServer is neither and it throws on every request. So
/// the scheme's handler is swapped for one that reads the caller's Windows identity from a header
/// — the handshake is the framework's business, and what Logrr actually owns is everything after
/// it: the identity → account mapping, the cookie, and where each outcome redirects.
/// </summary>
public class WindowsSignInRoutingTests
{
    private const string IdentityHeader = "X-Test-Windows-Account";

    private static WebApplicationFactory<Program> Factory(string dataPath, bool windowsAuth, bool autoSignIn = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Logrr:Storage:DataPath", dataPath)
                .UseSetting("Logrr:Auth:Windows:Enabled", windowsAuth ? "true" : "false")
                .UseSetting("Logrr:Auth:Windows:AutoSignIn", autoSignIn ? "true" : "false");

            if (windowsAuth)
            {
                b.ConfigureTestServices(s => s.Configure<AuthenticationOptions>(o =>
                    o.SchemeMap[NegotiateDefaults.AuthenticationScheme].HandlerType = typeof(StubWindowsHandler)));
            }
        });

    private static HttpClient NoRedirects(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "logrr-winroute-" + Guid.NewGuid().ToString("N"));

    /// <summary>Blazor's redirects are absolute; the endpoints' are relative. Compare like for like.</summary>
    private static string PathAndQuery(HttpResponseMessage resp)
    {
        var location = resp.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    /// <summary>Seed an account and return the client that signs in as <paramref name="identity"/>.</summary>
    private static HttpClient AsWindowsUser(WebApplicationFactory<Program> factory, string identity)
    {
        var client = NoRedirects(factory);
        client.DefaultRequestHeaders.Add(IdentityHeader, identity);
        return client;
    }

    private static void SeedUser(WebApplicationFactory<Program> factory, string username, string? windowsAccount, string? password = null)
    {
        using var scope = factory.Services.CreateScope();
        byte[]? hash = null, salt = null;
        if (password is not null)
        {
            (hash, salt) = PasswordHasher.Create(password);
        }
        scope.ServiceProvider.GetRequiredService<UserStore>().Create(new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            WindowsAccount = windowsAccount,
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = UserRole.User,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
    }

    [Fact]
    public async Task A_mapped_identity_is_signed_in_and_returned_to_where_it_came_from()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            SeedUser(factory, "jon", @"CONTOSO\jrhoades");
            var client = AsWindowsUser(factory, @"contoso\JRHOADES"); // casing as the host reports it

            var resp = await client.GetAsync("/auth/windows?returnUrl=%2Fapps%2Fbilling%2Ftail");

            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            Assert.Equal("/apps/billing/tail", resp.Headers.Location!.ToString());
            // The session is the same cookie the password form issues - nothing downstream changes.
            Assert.Contains(resp.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Cookies"));
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task An_identity_no_account_claims_is_refused_and_lands_on_the_form()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            SeedUser(factory, "jon", @"CONTOSO\jrhoades");
            var client = AsWindowsUser(factory, @"CONTOSO\contractor");

            var resp = await client.GetAsync("/auth/windows");

            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            var location = resp.Headers.Location!.ToString();
            Assert.Contains("/login?local=1", location);   // ?local=1 stops the auto-redirect looping
            Assert.Contains("windows=unmapped", location);
            Assert.False(resp.Headers.Contains("Set-Cookie")); // no account, no session
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task An_unidentified_caller_gets_a_challenge_rather_than_a_session()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/auth/windows"); // no identity header

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task A_signed_in_windows_user_can_reach_a_page_that_requires_a_session()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            SeedUser(factory, "jon", @"CONTOSO\jrhoades");
            var client = AsWindowsUser(factory, @"CONTOSO\jrhoades");

            var signIn = await client.GetAsync("/auth/windows");
            var cookie = signIn.Headers.GetValues("Set-Cookie").First().Split(';')[0];

            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("Cookie", cookie);
            var home = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, home.StatusCode); // not bounced to /login
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task A_windows_only_account_cannot_be_signed_into_with_a_password()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            SeedUser(factory, "svc", @"CONTOSO\svc-logrr"); // no password at all

            var resp = await NoRedirects(factory).PostAsync("/auth/login", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["username"] = "svc", ["password"] = "" }));

            Assert.Equal("/login?error=1", resp.Headers.Location!.ToString());
            Assert.False(resp.Headers.Contains("Set-Cookie"));
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task The_diagnostics_name_the_setting_when_the_feature_is_off()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: false);
        try
        {
            var html = await NoRedirects(factory).GetStringAsync("/login?local=1&diag=1");

            // The state that looks like nothing happening at all: no button, no redirect, no clue.
            Assert.Contains("Windows sign-in is turned off on this server", html);
            Assert.Contains("Logrr:Auth:Windows:Enabled", html);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task A_refused_test_returns_to_the_diagnostics_carrying_the_identity_it_saw()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var client = AsWindowsUser(factory, @"CONTOSO\contractor"); // linked to nothing

            var resp = await client.GetAsync("/auth/windows?diag=1&returnUrl=%2F");
            var location = PathAndQuery(resp);

            // Without diag=1 surviving the refusal, the one action that reports the identity
            // would land on a page that cannot show it.
            Assert.Contains("diag=1", location);
            Assert.Contains($"account={Uri.EscapeDataString(@"CONTOSO\contractor")}", location);

            var html = await client.GetStringAsync(location);
            Assert.Contains("A Windows sign-in was just refused", html);
            Assert.Contains(@"CONTOSO\contractor", html);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task Signing_out_does_not_hand_the_browser_straight_back_through_the_handshake()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var resp = await NoRedirects(factory).PostAsync("/auth/logout", null);

            Assert.Equal("/login?local=1", resp.Headers.Location!.ToString());
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task Windows_route_does_not_exist_while_the_feature_is_off()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: false);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/auth/windows");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task Sign_in_page_shows_the_form_when_the_feature_is_off()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: false);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Contains("/auth/login", await resp.Content.ReadAsStringAsync());
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task Auto_sign_in_sends_the_browser_through_the_windows_handshake()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/login?returnUrl=%2Fapps%2Fbilling%2Ftail");
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
            var location = PathAndQuery(resp);
            Assert.StartsWith("/auth/windows", location);
            Assert.Contains(Uri.EscapeDataString("/apps/billing/tail"), location);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task An_absolute_return_url_is_not_carried_into_the_handshake()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/login?returnUrl=https%3A%2F%2Fevil.example.com");
            Assert.Equal($"/auth/windows?returnUrl={Uri.EscapeDataString("/")}", PathAndQuery(resp));
        }
        finally { Cleanup(factory, path); }
    }

    [Theory]
    [InlineData("/login?local=1")]                       // sign-out, and every Windows failure path
    [InlineData("/login?local=1&windows=unmapped")]
    public async Task The_password_form_stays_reachable_so_a_bad_mapping_cannot_lock_everyone_out(string url)
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            var resp = await NoRedirects(factory).GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("/auth/login", html);   // the password form
            Assert.Contains("/auth/windows", html); // and an explicit way back to Windows
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task With_auto_sign_in_off_the_page_offers_windows_instead_of_forcing_it()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true, autoSignIn: false);
        try
        {
            var resp = await NoRedirects(factory).GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("/auth/windows", html);
            Assert.Contains("/auth/login", html);
        }
        finally { Cleanup(factory, path); }
    }

    [Fact]
    public async Task A_valid_api_key_is_not_challenged_by_windows_authentication()
    {
        var path = TempPath();
        using var factory = Factory(path, windowsAuth: true);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var apps = scope.ServiceProvider.GetRequiredService<AppStore>();
                var tokens = scope.ServiceProvider.GetRequiredService<TokenStore>();
                apps.Create(new AppRecord
                {
                    Id = "api-client", Name = "API client", MinimumLevel = Logrr.Contracts.LogLevel.Verbose,
                    IsEnabled = true, CreatedUtc = DateTimeOffset.UtcNow,
                });
                var secret = TokenSecret.Generate("api-client");
                tokens.Create(new TokenRecord
                {
                    Id = Guid.NewGuid().ToString("N"), AppId = "api-client",
                    Prefix = TokenSecret.Prefix(secret), Hash = TokenSecret.Hash(secret),
                    Scopes = TokenScopes.Ingest, CreatedUtc = DateTimeOffset.UtcNow,
                });

                var client = NoRedirects(factory);
                client.DefaultRequestHeaders.Add("X-Logrr-ApiKey", secret);
                var body = $"{{\"@t\":\"{DateTimeOffset.UtcNow:O}\",\"@mt\":\"API key accepted\"}}";

                var response = await client.PostAsync("/api/events/raw",
                    new StringContent(body, Encoding.UTF8, "application/vnd.serilog.clef"));

                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Empty(response.Headers.WwwAuthenticate);
            }
        }
        finally { Cleanup(factory, path); }
    }

    private static void Cleanup(WebApplicationFactory<Program> factory, string path)
    {
        factory.Dispose();
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Stands in for the Negotiate handler: the caller's Windows identity arrives in a header
    /// instead of a GSSAPI handshake. No header means an unidentified caller, so the default
    /// challenge (401) applies exactly as it would against a real host.
    /// </summary>
    private sealed class StubWindowsHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var account = Request.Headers[IdentityHeader].ToString();
            if (string.IsNullOrEmpty(account))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, account)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
