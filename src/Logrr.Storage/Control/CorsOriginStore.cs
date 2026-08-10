using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>A browser origin permitted to post logs cross-origin (managed in the admin UI).</summary>
public sealed record CorsOrigin(string Origin, DateTimeOffset CreatedUtc);

/// <summary>CRUD for the <c>cors_origins</c> table. Origins are stored normalised (see helper).</summary>
public sealed class CorsOriginStore(ControlDatabase db)
{
    public IReadOnlyList<CorsOrigin> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT origin, created_utc FROM cors_origins ORDER BY origin;";
        using var reader = cmd.ExecuteReader();
        var list = new List<CorsOrigin>();
        while (reader.Read())
        {
            list.Add(new CorsOrigin(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader.GetValue(1)))));
        }
        return list;
    }

    /// <summary>Insert an origin (idempotent). Returns false if it was already present.</summary>
    public bool Add(string origin, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = db.Dialect.IsSqlServer
            ? """
              INSERT INTO cors_origins (origin, created_utc)
              SELECT @o, @c
              WHERE NOT EXISTS (SELECT 1 FROM cors_origins WITH (UPDLOCK, SERIALIZABLE) WHERE origin = @o);
              """
            : "INSERT OR IGNORE INTO cors_origins (origin, created_utc) VALUES (@o, @c);";
        cmd.Add("@o", origin);
        cmd.Add("@c", now.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Remove(string origin)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cors_origins WHERE origin = @o;";
        cmd.Add("@o", origin);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Normalise user input to a CORS origin: <c>scheme://host[:port]</c>, lower-cased, no path or
    /// trailing slash. Returns null if it isn't a valid absolute http(s) origin.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        var text = input.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        // GetLeftPart(Authority) yields scheme://host:port with the default port omitted.
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
