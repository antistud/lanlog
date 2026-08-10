using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting.Server;

namespace Logrr.Server.Auth;

public enum CheckStatus
{
    Ok,
    Problem,
    Info,
}

/// <summary>One line of the sign-in page's Windows diagnostics: what is true, and what to do.</summary>
public sealed record Check(CheckStatus Status, string Title, string Detail, string? Fix = null);

/// <summary>
/// Answers "why am I looking at a password form?" from the server's own state (SPEC §11).
/// Windows sign-in spans three places that cannot see each other - IIS site config, Logrr's
/// appsettings, and the per-user account link - and a failure in any of them looks identical
/// from the browser. Everything here is readable without signing in, so it stays to booleans,
/// counts and the caller's own identity: no usernames, no paths, no secrets.
/// </summary>
public sealed class WindowsAuthDiagnostics(
    WindowsAuthOptions options,
    IAuthenticationSchemeProvider schemes,
    IEnumerable<IServerIntegratedAuth> serverIntegratedAuth,
    UserStore users)
{
    /// <param name="requestHost">Host header, for the browser-zone check.</param>
    /// <param name="currentIdentity">Identity on this request, if the host passed one through.</param>
    /// <param name="refusedAccount">Identity a just-refused sign-in reported, via the query string.</param>
    public async Task<IReadOnlyList<Check>> RunAsync(string? requestHost, string? currentIdentity, string? refusedAccount)
    {
        var checks = new List<Check>();

        // 1. The switch itself. Read once at startup, so an edited file that was never followed
        //    by a restart still reports "off" - which is the most common cause of "nothing happens".
        if (!options.Enabled)
        {
            checks.Add(new Check(CheckStatus.Problem,
                "Windows sign-in is turned off on this server",
                "The server started with Logrr:Auth:Windows:Enabled set to false, so there is no Windows sign-in route to use.",
                """Set "Auth": { "Windows": { "Enabled": true } } inside the "Logrr" section of appsettings.json in the deployed folder, then restart the app pool. This setting is read once at startup - editing the file alone changes nothing."""));
        }
        else
        {
            checks.Add(new Check(CheckStatus.Ok,
                "Windows sign-in is enabled",
                options.AutoSignIn
                    ? "Anonymous visitors are sent straight through the Windows handshake."
                    : "Auto sign-in is off, so this page shows a Sign in with Windows button instead of redirecting."));

            if (await schemes.GetSchemeAsync(NegotiateDefaults.AuthenticationScheme) is null)
            {
                checks.Add(new Check(CheckStatus.Problem,
                    "The Negotiate scheme is not registered",
                    "The setting is on but the authentication scheme is missing, which means the running process started before the setting was applied.",
                    "Restart the app pool so the app re-reads its configuration."));
            }
        }

        // 2. The host. Under IIS the module does the handshake and Logrr defers to it; under
        //    Kestrel or HTTP.sys Logrr does it itself. Only the first can be misconfigured in a
        //    way Logrr cannot see from its own settings.
        var integrated = serverIntegratedAuth.LastOrDefault();
        if (integrated is null)
        {
            checks.Add(new Check(CheckStatus.Info,
                "This host does not provide Windows authentication itself",
                "Normal when running under Kestrel or HTTP.sys - Logrr performs the handshake in-process. Under IIS you would see a different result here, so if this app is behind IIS, it is not hosted in-process as expected."));
        }
        else if (integrated.IsEnabled)
        {
            checks.Add(new Check(CheckStatus.Ok,
                "The host is providing Windows authentication",
                $"Requests arrive already authenticated by the host (scheme '{integrated.AuthenticationScheme}')."));
        }
        else
        {
            checks.Add(new Check(CheckStatus.Problem,
                "IIS is hosting Logrr but Windows Authentication is off for this site",
                "The handshake can never complete, and with Windows sign-in enabled the sign-in route will fail outright.",
                @"Enable both Windows Authentication and Anonymous Authentication on the site. The section is locked, so it cannot come from web.config: appcmd set config ""<site>"" -section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost"));
        }

        // 3. The per-user link. An identity nobody claims is refused by design, so zero links
        //    means Windows sign-in is working and still refusing everyone.
        var linked = users.List().Count(u => !string.IsNullOrEmpty(u.WindowsAccount));
        checks.Add(linked == 0
            ? new Check(CheckStatus.Problem,
                "No Logrr account is linked to a Windows account",
                "Windows sign-in only admits accounts that have been linked, so every identity is refused until at least one link exists.",
                "Sign in with a password as an admin, then set Admin -> Users -> Windows account, exactly as Windows reports it (e.g. CONTOSO\\jrhoades).")
            : new Check(CheckStatus.Ok,
                $"{linked} account{(linked == 1 ? " is" : "s are")} linked to a Windows account",
                "Each must match the identity the server sees, character for character (case aside)."));

        // 4. The identity itself - the single most useful fact here, because a link that does not
        //    match character for character fails exactly like no link at all.
        if (!string.IsNullOrEmpty(refusedAccount))
        {
            checks.Add(new Check(CheckStatus.Problem,
                "A Windows sign-in was just refused",
                $"The handshake worked and the server saw you as {refusedAccount}, but no Logrr account is linked to that identity.",
                $"Sign in with a password as an admin, then set Admin -> Users -> Windows account to exactly: {refusedAccount}"));
        }
        else if (!string.IsNullOrEmpty(currentIdentity))
        {
            checks.Add(new Check(CheckStatus.Info,
                "This request arrived with a Windows identity",
                $"The server sees you as {currentIdentity}. That exact string is what an account must be linked to."));
        }

        // 5. Silent SSO is a browser-side decision Logrr cannot influence, and a dotted host name
        //    is the usual reason a working setup still prompts (or fails outright).
        if (!string.IsNullOrEmpty(requestHost))
        {
            var name = requestHost.Split(':')[0];
            var dotted = name.Contains('.');
            checks.Add(dotted
                ? new Check(CheckStatus.Info,
                    $"This site is reached as '{name}'",
                    "Browsers only sign in silently for sites in the Local intranet zone, and a dotted name is not in it by default. Reaching the server by its short hostname usually resolves this.",
                    "Otherwise add the site to Local intranet (Internet Options -> Security -> Local intranet -> Sites -> Advanced), by Group Policy for everyone.")
                : new Check(CheckStatus.Ok,
                    $"This site is reached as '{name}'",
                    "A short hostname is normally already in the browser's Local intranet zone, which is what allows a silent sign-in."));
        }

        return checks;
    }
}
