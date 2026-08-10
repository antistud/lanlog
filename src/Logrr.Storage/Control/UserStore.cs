using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>CRUD for the <c>users</c> table (SPEC §11).</summary>
public sealed class UserStore(ControlDatabase db)
{
    public void Create(UserRecord user)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, username, password_hash, password_salt, windows_account, role,
                               must_change_password, app_access, created_utc)
            VALUES (@id, @username, @hash, @salt, @win, @role, @must, @access, @created);
            """;
        Bind(cmd, user);
        cmd.ExecuteNonQuery();
    }

    public UserRecord? GetByUsername(string username)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username = @u;";
        cmd.Add("@u", username);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// The account mapped to a Windows identity, or null if none is. Matching is case-insensitive
    /// because Windows account names are; the caller passes the name exactly as the server
    /// reported it (<c>DOMAIN\user</c>).
    /// </summary>
    public UserRecord? GetByWindowsAccount(string windowsAccount)
    {
        if (string.IsNullOrWhiteSpace(windowsAccount))
        {
            return null;
        }
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT * FROM users WHERE {db.Dialect.CaseInsensitiveEquals("windows_account", "@w")};";
        cmd.Add("@w", windowsAccount.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<UserRecord> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users ORDER BY username;";
        using var reader = cmd.ExecuteReader();
        var users = new List<UserRecord>();
        while (reader.Read())
        {
            users.Add(Map(reader));
        }
        return users;
    }

    public bool AnyExist()
    {
        using var conn = db.Open();
        return Db.Scalar(conn, db.Dialect.AnyRowsScalar("users")) != 0;
    }

    public void Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = @id;";
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePassword(string id, byte[] hash, byte[] salt, bool mustChange)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET password_hash = @hash, password_salt = @salt,
                             must_change_password = @must
            WHERE id = @id;
            """;
        cmd.Add("@hash", hash);
        cmd.Add("@salt", salt);
        cmd.Add("@must", mustChange ? 1 : 0);
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Map (or, with null, unmap) the Windows identity this account signs in as. Returns false if
    /// another account already claims it - the unique index would otherwise throw.
    /// </summary>
    public bool SetWindowsAccount(string id, string? windowsAccount)
    {
        var normalized = string.IsNullOrWhiteSpace(windowsAccount) ? null : windowsAccount.Trim();
        if (normalized is not null && GetByWindowsAccount(normalized) is { } other && other.Id != id)
        {
            return false;
        }

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET windows_account = @w WHERE id = @id;";
        cmd.Add("@w", (object?)normalized);
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
        return true;
    }

    private static void Bind(DbCommand cmd, UserRecord u)
    {
        cmd.Add("@id", u.Id);
        cmd.Add("@username", u.Username);
        cmd.Add("@hash", (object?)u.PasswordHash);
        cmd.Add("@salt", (object?)u.PasswordSalt);
        cmd.Add("@win", (object?)(string.IsNullOrWhiteSpace(u.WindowsAccount) ? null : u.WindowsAccount.Trim()));
        cmd.Add("@role", (int)u.Role);
        cmd.Add("@must", u.MustChangePassword ? 1 : 0);
        cmd.Add("@access", u.AppAccess is null ? null : System.Text.Json.JsonSerializer.Serialize(u.AppAccess));
        cmd.Add("@created", u.CreatedUtc.ToUnixTimeMilliseconds());
    }

    private static UserRecord Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Username = r.GetString(r.GetOrdinal("username")),
        PasswordHash = r.Bytes("password_hash"),
        PasswordSalt = r.Bytes("password_salt"),
        WindowsAccount = r.Str("windows_account"),
        Role = (UserRole)r.Int32("role"),
        MustChangePassword = r.Bool("must_change_password"),
        AppAccess = r.IsDBNull(r.GetOrdinal("app_access"))
            ? null
            : AppStore.ReadStringList(r, "app_access"),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.Int64("created_utc")),
    };
}
