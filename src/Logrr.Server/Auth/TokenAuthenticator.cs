using System.Collections.Concurrent;
using Logrr.Contracts;
using Logrr.Server.Security;
using Logrr.Storage.Control;
using Microsoft.Extensions.Caching.Memory;

namespace Logrr.Server.Auth;

public enum AuthFailure { None, Missing, Invalid, Forbidden }

/// <summary>Resolved caller identity for an ingest/read request.</summary>
public sealed record AuthContext(TokenRecord Token, AppRecord App);

public sealed record AuthResult(AuthContext? Context, AuthFailure Failure)
{
    public bool Ok => Context is not null;
    public static AuthResult Success(AuthContext ctx) => new(ctx, AuthFailure.None);
    public static AuthResult Fail(AuthFailure failure) => new(null, failure);
}

/// <summary>
/// Resolves a token from request headers (SPEC §6.1, §11). A <see cref="MemoryCache"/> with
/// a 30 s TTL keeps the hot ingest path off <c>control.db</c>; revocation propagates within
/// one TTL. Lookup is by prefix; verification is a constant-time hash compare.
/// </summary>
public sealed class TokenAuthenticator(TokenStore tokens, AppStore apps, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StampInterval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStamped = new();

    private sealed record CachedToken(TokenRecord Token, AppRecord App);

    public AuthResult Authenticate(HttpContext http, TokenScopes required)
    {
        var secret = ExtractSecret(http.Request);
        if (string.IsNullOrEmpty(secret))
        {
            return AuthResult.Fail(AuthFailure.Missing);
        }

        var prefix = TokenSecret.Prefix(secret);
        var entry = cache.GetOrCreate(prefix, e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            // Fall back to the retired prefix scheme so tokens already in the field keep
            // working; the hash compare below is what actually accepts them either way.
            var token = tokens.FindByPrefix(prefix)
                ?? tokens.FindByPrefix(TokenSecret.LegacyPrefix(secret));
            if (token is null)
            {
                return null;
            }
            var app = apps.Get(token.AppId);
            return app is null ? null : new CachedToken(token, app);
        });

        if (entry is null || !TokenSecret.Verify(secret, entry.Token.Hash))
        {
            return AuthResult.Fail(AuthFailure.Invalid);
        }

        var now = DateTimeOffset.UtcNow;
        if (!entry.Token.IsActive(now))
        {
            return AuthResult.Fail(AuthFailure.Invalid);
        }
        if ((entry.Token.Scopes & required) != required)
        {
            return AuthResult.Fail(AuthFailure.Forbidden);
        }
        if (!entry.App.IsEnabled && required.HasFlag(TokenScopes.Ingest))
        {
            return AuthResult.Fail(AuthFailure.Forbidden); // disabled app → ingest 403 (SPEC §5.1)
        }

        StampLastUsed(entry.Token.Id, now);
        return AuthResult.Success(new AuthContext(entry.Token, entry.App));
    }

    // Update last-used at most once a minute per token, off the request thread (SPEC §5.2).
    private void StampLastUsed(string tokenId, DateTimeOffset now)
    {
        var last = _lastStamped.GetOrAdd(tokenId, DateTimeOffset.MinValue);
        if (now - last < StampInterval)
        {
            return;
        }
        if (_lastStamped.TryUpdate(tokenId, now, last))
        {
            _ = Task.Run(() =>
            {
                try { tokens.TouchLastUsed(tokenId, now); } catch { /* best-effort */ }
            });
        }
    }

    private static string? ExtractSecret(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Logrr-ApiKey", out var v1) && v1.Count > 0)
        {
            return v1[0];
        }
        if (request.Headers.TryGetValue("X-Seq-ApiKey", out var v2) && v2.Count > 0)
        {
            return v2[0];
        }
        var auth = request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth["Bearer ".Length..].Trim() : null;
    }
}
