using System.Security.Claims;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Logrr.Server.Auth;

public static class AuthEndpoints
{
    /// <summary>Claim carried while an account still owes a password change (SPEC §2, §11).</summary>
    public const string MustChangeClaim = "logrr:must_change";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
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
            return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        }).DisableAntiforgery();

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
            return Results.Redirect("/login");
        });
    }

    private static async Task SignIn(HttpContext http, UserRecord user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MustChangeClaim, "1"));
        }
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

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
