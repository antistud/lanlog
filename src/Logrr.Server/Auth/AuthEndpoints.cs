using System.Security.Claims;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Logrr.Server.Auth;

public static class AuthEndpoints
{
    /// <summary>Claim carried while an account still owes a password change (SPEC §2, §11).</summary>
    public const string MustChangeClaim = "logrr:must_change";

    /// <summary>Set on sessions established by Windows integrated sign-in (SPEC §11).</summary>
    public const string WindowsSignInClaim = "logrr:windows";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var windows = app.ServiceProvider.GetRequiredService<WindowsAuthOptions>();

        app.MapPost("/auth/login", async (HttpContext http, UserStore users, StoragePaths paths,
            [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl) =>
        {
            var user = users.GetByUsername(username);
            if (user?.PasswordHash is null || user.PasswordSalt is null ||
                !PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt))
            {
                return Results.Redirect("/login?error=1");
            }

            await SignIn(http, user);
            TryDeleteCredentialsFile(paths); // first sign-in removes the credentials drop file (SPEC §2)

            // Force the change before anything else if the account still owes one.
            if (user.MustChangePassword)
            {
                return Results.Redirect("/account/password");
            }
            return Results.Redirect(LocalOrRoot(returnUrl));
        }).DisableAntiforgery();

        if (windows.Enabled)
        {
            MapWindowsSignIn(app);
        }

        app.MapPost("/account/change-password", async (HttpContext http, UserStore users,
            [FromForm] string currentPassword, [FromForm] string newPassword, [FromForm] string confirmPassword) =>
        {
            var username = http.User.Identity?.Name;
            var user = username is null ? null : users.GetByUsername(username);
            if (user?.PasswordHash is null || user.PasswordSalt is null)
            {
                return Results.Redirect("/login");
            }

            if (!PasswordHasher.Verify(currentPassword, user.PasswordHash, user.PasswordSalt))
            {
                return Results.Redirect("/account/password?error=current");
            }
            if (newPassword.Length < 8)
            {
                return Results.Redirect("/account/password?error=length");
            }
            if (newPassword != confirmPassword)
            {
                return Results.Redirect("/account/password?error=match");
            }

            var (hash, salt) = PasswordHasher.Create(newPassword);
            users.UpdatePassword(user.Id, hash, salt, mustChange: false);

            // Re-issue the cookie without the must-change claim so the gate clears immediately.
            await SignIn(http, user with { MustChangePassword = false });
            return Results.Redirect("/?changed=1");
        }).RequireAuthorization().DisableAntiforgery();

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // With Windows sign-in on, a plain /login would hand the browser straight back through
            // the handshake and sign the user in again - "sign out" that does nothing. Land on the
            // form instead and let them choose.
            return Results.Redirect(windows.Enabled ? "/login?local=1" : "/login");
        });
    }

    /// <summary>
    /// Windows integrated sign-in (SPEC §11). The Negotiate challenge identifies the caller; the
    /// identity is then matched against an existing account's <c>windows_account</c> and the normal
    /// session cookie is issued. An identity nobody claims is *not* an account - it falls back to
    /// the sign-in form rather than being provisioned.
    /// </summary>
    private static void MapWindowsSignIn(IEndpointRouteBuilder app)
    {
        // Explicitly scheme-scoped: the app's default scheme is the cookie, and an anonymous
        // visitor here must get the Negotiate challenge, not a redirect back to /login.
        var negotiateOnly = new AuthorizationPolicyBuilder(NegotiateDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .Build();

        app.MapGet("/auth/windows", async (HttpContext http, UserStore users, StoragePaths paths,
            ILoggerFactory loggerFactory, string? returnUrl) =>
        {
            var log = loggerFactory.CreateLogger("Logrr.Auth.Windows");
            var account = http.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(account))
            {
                // Authorized but nameless: the host let the request through without an identity.
                log.LogWarning("Windows sign-in produced no account name; check the host's Windows authentication settings.");
                return Results.Redirect("/login?local=1&windows=anonymous");
            }

            var user = users.GetByWindowsAccount(account);
            if (user is null)
            {
                log.LogInformation("Windows sign-in refused: no Logrr account is mapped to {Account}.", account);
                return Results.Redirect($"/login?local=1&windows=unmapped&account={Uri.EscapeDataString(account)}");
            }

            await SignIn(http, user, viaWindows: true);
            TryDeleteCredentialsFile(paths);
            log.LogInformation("Windows sign-in for {Account} as Logrr user {User}.", account, user.Username);

            // A Windows-only account has no password to change, so the gate would be a dead end.
            if (user.MustChangePassword && user.PasswordHash is not null)
            {
                return Results.Redirect("/account/password");
            }
            return Results.Redirect(LocalOrRoot(returnUrl));
        }).RequireAuthorization(negotiateOnly);
    }

    private static async Task SignIn(HttpContext http, UserRecord user, bool viaWindows = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        // Only gate on a password change there is a password to change - a Windows-only account
        // would otherwise be stuck on a form it can never satisfy.
        if (user.MustChangePassword && user.PasswordHash is not null)
        {
            claims.Add(new Claim(MustChangeClaim, "1"));
        }
        if (viaWindows)
        {
            claims.Add(new Claim(WindowsSignInClaim, "1"));
        }
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>
    /// Confine a post-sign-in redirect to this site. The return URL arrives from the query string
    /// (or a form field), so an absolute or protocol-relative value would turn the sign-in route
    /// into an open redirect.
    /// </summary>
    private static string LocalOrRoot(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    private static void TryDeleteCredentialsFile(StoragePaths paths)
    {
        try
        {
            var file = Path.Combine(paths.DataRoot, "FIRST-RUN-CREDENTIALS.txt");
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // Non-fatal.
        }
    }
}
