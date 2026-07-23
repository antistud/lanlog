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

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            // First successful sign-in: remove the credentials drop file (SPEC §2).
            TryDeleteCredentialsFile(paths);

            return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
        }).DisableAntiforgery();

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
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
