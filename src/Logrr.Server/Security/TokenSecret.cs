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
    public const int PrefixLength = 8;

    public static string Generate(string appId)
    {
        Span<char> random = stackalloc char[22];
        for (var i = 0; i < random.Length; i++)
        {
            random[i] = Base62[RandomNumberGenerator.GetInt32(Base62.Length)];
        }
        return $"lg_{appId}_{new string(random)}";
    }

    public static string Prefix(string secret) =>
        secret.Length >= PrefixLength ? secret[..PrefixLength] : secret;

    public static byte[] Hash(string secret) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    public static bool Verify(string secret, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(secret), expectedHash);
}
