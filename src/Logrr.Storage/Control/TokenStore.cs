using System.Data.Common;
using Logrr.Contracts;
using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>CRUD + lookup for the <c>tokens</c> table (SPEC §5.2, §11).</summary>
public sealed class TokenStore(ControlDatabase db)
{
    public void Create(TokenRecord token)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tokens (id, app_id, name, prefix, hash, scopes,
                                expires_utc, last_used_utc, revoked_utc, created_utc)
            VALUES (@id, @app, @name, @prefix, @hash, @scopes,
                    @expires, @lastUsed, @revoked, @created);
            """;
        cmd.Add("@id", token.Id);
        cmd.Add("@app", token.AppId);
        cmd.Add("@name", token.Name);
        cmd.Add("@prefix", token.Prefix);
        cmd.Add("@hash", token.Hash);
        cmd.Add("@scopes", (int)token.Scopes);
        cmd.Add("@expires", token.ExpiresUtc?.ToUnixTimeMilliseconds());
        cmd.Add("@lastUsed", token.LastUsedUtc?.ToUnixTimeMilliseconds());
        cmd.Add("@revoked", token.RevokedUtc?.ToUnixTimeMilliseconds());
        cmd.Add("@created", token.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Lookup by prefix — the plaintext first 8 chars used on the hot path.</summary>
    public TokenRecord? FindByPrefix(string prefix)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tokens WHERE prefix = @prefix;";
        cmd.Add("@prefix", prefix);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<TokenRecord> ListByApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM tokens WHERE app_id = @app ORDER BY created_utc DESC;";
        cmd.Add("@app", appId);
        using var reader = cmd.ExecuteReader();
        var tokens = new List<TokenRecord>();
        while (reader.Read())
        {
            tokens.Add(Map(reader));
        }
        return tokens;
    }

    public void Revoke(string id, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET revoked_utc = @now WHERE id = @id;";
        cmd.Add("@now", now.ToUnixTimeMilliseconds());
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Stamp last-used, at most once a minute (SPEC §5.2); called out of band.</summary>
    public void TouchLastUsed(string id, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tokens SET last_used_utc = @now WHERE id = @id;";
        cmd.Add("@now", now.ToUnixTimeMilliseconds());
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static TokenRecord Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        AppId = r.GetString(r.GetOrdinal("app_id")),
        Name = r.Str("name"),
        Prefix = r.GetString(r.GetOrdinal("prefix")),
        Hash = r.Bytes("hash")!,
        Scopes = (TokenScopes)r.Int32("scopes"),
        ExpiresUtc = ReadTs(r, "expires_utc"),
        LastUsedUtc = ReadTs(r, "last_used_utc"),
        RevokedUtc = ReadTs(r, "revoked_utc"),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.Int64("created_utc")),
    };

    internal static DateTimeOffset? ReadTs(DbDataReader r, string column) =>
        r.LongNull(column) is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
