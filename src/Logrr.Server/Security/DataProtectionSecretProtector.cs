using Logrr.Notify;
using Microsoft.AspNetCore.DataProtection;

namespace Logrr.Server.Security;

/// <summary>
/// <see cref="ISecretProtector"/> backed by ASP.NET Data Protection (SPEC §10.1, §11). Keys
/// are persisted to <c>{data}\keys</c> so destination secrets survive a recycle/redeploy.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Logrr.DestinationSecrets.v1");

    public byte[]? Protect(string? plaintext) =>
        plaintext is null ? null : _protector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(byte[]? ciphertext)
    {
        if (ciphertext is null)
        {
            return null;
        }
        try
        {
            return System.Text.Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // key lost or ciphertext corrupt (SPEC §11 backup caveat)
        }
    }
}
