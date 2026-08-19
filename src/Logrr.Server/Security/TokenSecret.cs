using System.Security.Cryptography;

namespace Logrr.Server.Security;

/// <summary>
/// Generates and hashes ingest/read token secrets (SPEC §5.2). Format
/// <c>lg_{appId}_{22 base62}</c>; the slug is a human convenience and is never trusted —
/// lookup is by prefix, verification by constant-time SHA-256 compare.
/// </summary>
public static class TokenSecret
{
    private const string Base62 = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>How much of the random tail the prefix carries.</summary>
    public const int PrefixLength = 8;

    /// <summary>Prefix length of tokens issued before the prefix covered the random tail.</summary>
    private const int LegacyPrefixLength = 8;

    public static string Generate(string appId)
    {
        Span<char> random = stackalloc char[22];
        for (var i = 0; i < random.Length; i++)
        {
            random[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }
        return $"lg_{appId}_{new string(random)}";
    }

    /// <summary>
    /// The stored lookup key: the <c>lg_{appId}_</c> head plus the first
    /// <see cref="PrefixLength"/> characters of the random tail.
    /// <para>
    /// The tail is what makes this unique. A prefix cut from the head alone is identical for
    /// every token of an app, and <c>tokens.prefix</c> is UNIQUE — so the app's second token
    /// could never be stored. Lookup is exact-match on this same function of the secret, so the
    /// head can stay in for legibility: an operator reading the token list can tell which app a
    /// row belongs to. 14 random characters (~83 bits) stay behind the hash.
    /// </para>
    /// </summary>
    public static string Prefix(string secret)
    {
        var tail = TailStart(secret);
        return tail < 0 || secret.Length - tail < PrefixLength
            ? secret // malformed: hand it back whole, so lookup misses rather than throws
            : secret[..(tail + PrefixLength)];
    }

    /// <summary>
    /// Prefix scheme used before <see cref="Prefix"/> reached the random tail — the head's
    /// first 8 characters. Kept so tokens issued under it keep authenticating; the UNIQUE
    /// constraint means at most one such row exists per app, so the match is unambiguous, and
    /// the hash compare still gates acceptance either way. Nothing writes this scheme any more.
    /// </summary>
    public static string LegacyPrefix(string secret) =>
        secret.Length >= LegacyPrefixLength ? secret[..LegacyPrefixLength] : secret;

    /// <summary>
    /// Index just past the second underscore — where the random tail begins. App ids are
    /// <c>[a-z0-9-]{3,32}</c> and carry no underscore of their own, so the second one always
    /// closes the head.
    /// </summary>
    private static int TailStart(string secret)
    {
        if (!secret.StartsWith("lg_", StringComparison.Ordinal))
        {
            return -1;
        }
        var sep = secret.IndexOf('_', 3);
        return sep < 0 ? -1 : sep + 1;
    }

    public static byte[] Hash(string secret) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    public static bool Verify(string secret, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(secret), expectedHash);
}
