using System.Security.Cryptography;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Server.Bootstrap;

/// <summary>
/// First-run bootstrap (SPEC §2): create the control schema, seed an <c>admin</c> account
/// with a generated password written to <c>FIRST-RUN-CREDENTIALS.txt</c>, and force a change
/// on first sign-in.
/// </summary>
public sealed class FirstRunBootstrapper(
    ControlDatabase db, UserStore users, StoragePaths paths, ILogger<FirstRunBootstrapper> logger)
{
    public void Run()
    {
        db.Initialize(); // idempotent: creates data root + runs migrations

        if (users.AnyExist())
        {
            return;
        }

        var password = GeneratePassword();
        var (hash, salt) = PasswordHasher.Create(password);
        users.Create(new UserRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = "admin",
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = UserRole.Admin,
            MustChangePassword = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var file = Path.Combine(paths.DataRoot, "FIRST-RUN-CREDENTIALS.txt");
        try
        {
            File.WriteAllText(file,
                $"Logrr first-run admin credentials\r\n\r\nUsername: admin\r\nPassword: {password}\r\n\r\n" +
                "You will be required to change this on first sign-in. Delete this file afterwards.\r\n");
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write FIRST-RUN-CREDENTIALS.txt");
        }

        // Also emit to the internal log so it is recoverable if the file write failed.
        logger.LogWarning("First-run admin created. Username 'admin', password '{Password}'. " +
                          "Change it on first sign-in.", password);
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[20];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }
}
