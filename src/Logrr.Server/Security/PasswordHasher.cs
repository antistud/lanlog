using System.Security.Cryptography;

namespace Logrr.Server.Security;

/// <summary>PBKDF2 password hashing (SPEC §11): Rfc2898DeriveBytes, SHA-256, 600k iterations.</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (byte[] Hash, byte[] Salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (hash, salt);
    }

    public static bool Verify(string password, byte[] hash, byte[] salt)
    {
        var computed = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return CryptographicOperations.FixedTimeEquals(computed, hash);
    }
}
