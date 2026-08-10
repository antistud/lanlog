using System.Text.Json;
using Logrr.Storage.Control;
using System.Data.Common;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

/// <summary>CRUD + circuit-breaker state for the <c>destinations</c> table (SPEC §10.1).</summary>
public sealed class DestinationStore(ControlDatabase db)
{
    public void Create(Destination d)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO destinations (id, name, kind, url, method, content_type, headers,
              auth_mode, auth_secret, auth_header_name, body_template,
              ticket_id_path, ticket_url_path, timeout_seconds, max_attempts,
              rate_limit_per_hour, is_enabled, consecutive_failures, circuit_open_until_utc, created_utc,
              smtp_host, smtp_port, smtp_security, smtp_username, smtp_from, smtp_to, smtp_subject)
            VALUES (@id, @name, @kind, @url, @method, @ct, @headers,
              @auth, @secret, @authHeader, @body,
              @idPath, @urlPath, @timeout, @maxAtt,
              @rate, @en, 0, NULL, @created,
              @smtpHost, @smtpPort, @smtpSec, @smtpUser, @smtpFrom, @smtpTo, @smtpSubject);
            """;
        cmd.P("@id", d.Id);
        cmd.P("@name", d.Name);
        cmd.P("@kind", (int)d.Kind);
        cmd.P("@url", d.Url);
        cmd.P("@method", d.Method);
        cmd.P("@ct", d.ContentType);
        cmd.P("@headers", JsonSerializer.Serialize(d.Headers));
        cmd.P("@auth", (int)d.AuthMode);
        cmd.P("@secret", (object?)d.AuthSecret);
        cmd.P("@authHeader", d.AuthHeaderName);
        cmd.P("@body", d.BodyTemplate);
        cmd.P("@idPath", d.TicketIdPath);
        cmd.P("@urlPath", d.TicketUrlPath);
        cmd.P("@timeout", d.TimeoutSeconds);
        cmd.P("@maxAtt", d.MaxAttempts);
        cmd.P("@rate", d.RateLimitPerHour);
        cmd.P("@en", d.IsEnabled ? 1 : 0);
        cmd.P("@created", d.CreatedUtc.Ms());
        cmd.P("@smtpHost", d.SmtpHost);
        cmd.P("@smtpPort", d.SmtpPort);
        cmd.P("@smtpSec", (int)d.SmtpSecurity);
        cmd.P("@smtpUser", d.SmtpUsername);
        cmd.P("@smtpFrom", d.SmtpFrom);
        cmd.P("@smtpTo", d.SmtpTo);
        cmd.P("@smtpSubject", d.SmtpSubjectTemplate);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Update the editable fields of a destination. Deliberately does NOT touch the
    /// encrypted secret (use <see cref="UpdateSecret"/>) or the circuit-breaker state.
    /// </summary>
    public void Update(Destination d)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE destinations SET
              name=@name, kind=@kind, url=@url, method=@method, content_type=@ct, headers=@headers,
              auth_mode=@auth, auth_header_name=@authHeader, body_template=@body,
              ticket_id_path=@idPath, ticket_url_path=@urlPath, timeout_seconds=@timeout,
              max_attempts=@maxAtt, rate_limit_per_hour=@rate, is_enabled=@en,
              smtp_host=@smtpHost, smtp_port=@smtpPort, smtp_security=@smtpSec,
              smtp_username=@smtpUser, smtp_from=@smtpFrom, smtp_to=@smtpTo, smtp_subject=@smtpSubject
            WHERE id=@id;
            """;
        cmd.P("@id", d.Id);
        cmd.P("@name", d.Name);
        cmd.P("@kind", (int)d.Kind);
        cmd.P("@url", d.Url);
        cmd.P("@method", d.Method);
        cmd.P("@ct", d.ContentType);
        cmd.P("@headers", JsonSerializer.Serialize(d.Headers));
        cmd.P("@auth", (int)d.AuthMode);
        cmd.P("@authHeader", d.AuthHeaderName);
        cmd.P("@body", d.BodyTemplate);
        cmd.P("@idPath", d.TicketIdPath);
        cmd.P("@urlPath", d.TicketUrlPath);
        cmd.P("@timeout", d.TimeoutSeconds);
        cmd.P("@maxAtt", d.MaxAttempts);
        cmd.P("@rate", d.RateLimitPerHour);
        cmd.P("@en", d.IsEnabled ? 1 : 0);
        cmd.P("@smtpHost", d.SmtpHost);
        cmd.P("@smtpPort", d.SmtpPort);
        cmd.P("@smtpSec", (int)d.SmtpSecurity);
        cmd.P("@smtpUser", d.SmtpUsername);
        cmd.P("@smtpFrom", d.SmtpFrom);
        cmd.P("@smtpTo", d.SmtpTo);
        cmd.P("@smtpSubject", d.SmtpSubjectTemplate);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Replace the encrypted secret only when the operator enters a new one.</summary>
    public void UpdateSecret(string id, byte[]? secret)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE destinations SET auth_secret = @secret WHERE id = @id;";
        cmd.P("@secret", (object?)secret);
        cmd.P("@id", id);
        cmd.ExecuteNonQuery();
    }

    public Destination? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM destinations WHERE id = @id;";
        cmd.P("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<Destination> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM destinations ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<Destination>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Record a failed attempt; open the circuit after the threshold (SPEC §10.5).</summary>
    public void RecordFailure(string id, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE destinations
            SET consecutive_failures = consecutive_failures + 1,
                circuit_open_until_utc = CASE
                  WHEN consecutive_failures + 1 >= @threshold THEN @openUntil
                  ELSE circuit_open_until_utc END
            WHERE id = @id;
            """;
        cmd.P("@threshold", NotifyOptions.CircuitFailureThreshold);
        cmd.P("@openUntil", now.Add(NotifyOptions.CircuitOpenDuration).Ms());
        cmd.P("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reset failure count and close the circuit on a successful delivery.</summary>
    public void RecordSuccess(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE destinations SET consecutive_failures = 0, circuit_open_until_utc = NULL WHERE id = @id;";
        cmd.P("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM destinations WHERE id = @id;";
        cmd.P("@id", id);
        cmd.ExecuteNonQuery();
    }

    internal static Destination Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Kind = (DestinationKind)r.Int32("kind"),
        Url = r.GetString(r.GetOrdinal("url")),
        Method = r.GetString(r.GetOrdinal("method")),
        ContentType = r.GetString(r.GetOrdinal("content_type")),
        Headers = r.Str("headers") is { } h && h.Length > 0
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(h) ?? new()
            : new Dictionary<string, string>(),
        AuthMode = (AuthMode)r.Int32("auth_mode"),
        AuthSecret = r.Bytes("auth_secret"),
        AuthHeaderName = r.Str("auth_header_name"),
        BodyTemplate = r.GetString(r.GetOrdinal("body_template")),
        TicketIdPath = r.Str("ticket_id_path"),
        TicketUrlPath = r.Str("ticket_url_path"),
        TimeoutSeconds = r.Int32("timeout_seconds"),
        MaxAttempts = r.Int32("max_attempts"),
        RateLimitPerHour = r.Int32("rate_limit_per_hour"),
        IsEnabled = r.Bool("is_enabled"),
        ConsecutiveFailures = r.Int32("consecutive_failures"),
        CircuitOpenUntilUtc = r.ReadTsNull("circuit_open_until_utc"),
        CreatedUtc = r.ReadTs("created_utc"),
        SmtpHost = r.Str("smtp_host"),
        SmtpPort = r.Int32("smtp_port"),
        SmtpSecurity = (SmtpSecurity)r.Int32("smtp_security"),
        SmtpUsername = r.Str("smtp_username"),
        SmtpFrom = r.Str("smtp_from"),
        SmtpTo = r.Str("smtp_to"),
        SmtpSubjectTemplate = r.GetString(r.GetOrdinal("smtp_subject")),
    };
}
