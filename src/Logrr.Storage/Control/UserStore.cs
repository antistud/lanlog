using Microsoft.Data.Sqlite;

namespace Logrr.Storage.Control;

/// <summary>CRUD for the <c>users</c> table (SPEC §11).</summary>
public sealed class UserStore(ControlDatabase db)
{
    public void Create(UserRecord user)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (id, username, password_hash, password_salt, role,
                               must_change_password, app_access, created_utc)
            VALUES ($id, $username, $hash, $salt, $role, $must, $access, $created);
            """;
        Bind(cmd, user);
        cmd.ExecuteNonQuery();
    }

    public UserRecord? GetByUsername(string username)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM users WHERE username = $u;";
        cmd.Add("$u", username);
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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM users);";
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    public void Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE id = $id;";
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePassword(string id, byte[] hash, byte[] salt, bool mustChange)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET password_hash = $hash, password_salt = $salt,
                             must_change_password = $must
            WHERE id = $id;
            """;
        cmd.Add("$hash", hash);
        cmd.Add("$salt", salt);
        cmd.Add("$must", mustChange ? 1 : 0);
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, UserRecord u)
    {
        cmd.Add("$id", u.Id);
        cmd.Add("$username", u.Username);
        cmd.Add("$hash", (object?)u.PasswordHash);
        cmd.Add("$salt", (object?)u.PasswordSalt);
        cmd.Add("$role", (int)u.Role);
        cmd.Add("$must", u.MustChangePassword ? 1 : 0);
        cmd.Add("$access", u.AppAccess is null ? null : System.Text.Json.JsonSerializer.Serialize(u.AppAccess));
        cmd.Add("$created", u.CreatedUtc.ToUnixTimeMilliseconds());
    }

    private static UserRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Username = r.GetString(r.GetOrdinal("username")),
        PasswordHash = r.IsDBNull(r.GetOrdinal("password_hash")) ? null : (byte[])r["password_hash"],
        PasswordSalt = r.IsDBNull(r.GetOrdinal("password_salt")) ? null : (byte[])r["password_salt"],
        Role = (UserRole)r.GetInt32(r.GetOrdinal("role")),
        MustChangePassword = r.GetInt32(r.GetOrdinal("must_change_password")) != 0,
        AppAccess = r.IsDBNull(r.GetOrdinal("app_access"))
            ? null
            : AppStore.ReadStringList(r, "app_access"),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_utc"))),
    };
}
