namespace Logrr.Storage.Sql;

/// <summary>
/// The control schema for SQL Server (SPEC §4.4, §4.7) — migration for migration, the same
/// list as <see cref="Control.ControlSchema"/>, so a version number means the same thing on
/// either backend.
/// </summary>
/// <remarks>
/// The translation is mechanical apart from types and one index. SQLite's storage classes are
/// advisory, so the original DDL leans on <c>INTEGER</c> for booleans, enums and unix
/// timestamps alike; here those become <c>BIT</c>, <c>INT</c> and <c>BIGINT</c> respectively,
/// and <c>BLOB</c> becomes <c>VARBINARY(MAX)</c>. Every store binds booleans as 0/1 and
/// timestamps as unix milliseconds regardless of backend, so nothing above this file changes.
/// The one judgement call is the Windows-account index (v7): SQLite gets case-insensitivity
/// from <c>COLLATE NOCASE</c>, while here the column carries a CI collation directly, which
/// makes the unique index case-insensitive by construction.
/// </remarks>
public static class SqlServerControlSchema
{
    // The mustache defaults, mirrored from ControlSchema. Held as constants because they are
    // the only literal braces in the file: these migrations are raw *interpolated* strings, so
    // `{` opens an interpolation and there is no `{{` escape - spelling them inline would force
    // a different `$` count across the whole migration just to quote two template strings.
    private const string DedupeKeyDefault = "{{event.eventType}}";
    private const string SmtpSubjectDefault = "[{{app.name}}] {{event.level}}: {{event.message}}";

    /// <summary>Ordered migrations for a given schema. Index + 1 is the resulting version.</summary>
    public static IReadOnlyList<string> Migrations(string schema) =>
    [
        // v1 — initial schema.
        $"""
        CREATE TABLE [{schema}].[apps] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL, description NVARCHAR(MAX) NULL,
          retention_days INT NOT NULL, max_size_mb INT NOT NULL,
          minimum_level INT NOT NULL, indexed_properties NVARCHAR(MAX) NULL,
          is_enabled BIT NOT NULL, created_utc BIGINT NOT NULL
        );

        CREATE TABLE [{schema}].[tokens] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, app_id NVARCHAR(64) NOT NULL REFERENCES [{schema}].[apps](id),
          name NVARCHAR(256) NULL, prefix NVARCHAR(64) NOT NULL UNIQUE, hash VARBINARY(MAX) NOT NULL,
          scopes INT NOT NULL, expires_utc BIGINT NULL, last_used_utc BIGINT NULL,
          revoked_utc BIGINT NULL, created_utc BIGINT NOT NULL
        );

        CREATE TABLE [{schema}].[users] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, username NVARCHAR(256) NOT NULL UNIQUE,
          password_hash VARBINARY(MAX) NULL, password_salt VARBINARY(MAX) NULL, role INT NOT NULL,
          must_change_password BIT NOT NULL, app_access NVARCHAR(MAX) NULL, created_utc BIGINT NOT NULL
        );

        CREATE TABLE [{schema}].[destinations] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL, url NVARCHAR(2048) NOT NULL,
          method NVARCHAR(16) NOT NULL DEFAULT 'POST',
          content_type NVARCHAR(128) NOT NULL DEFAULT 'application/json',
          headers NVARCHAR(MAX) NULL,
          auth_mode INT NOT NULL, auth_secret VARBINARY(MAX) NULL, auth_header_name NVARCHAR(128) NULL,
          body_template NVARCHAR(MAX) NOT NULL,
          ticket_id_path NVARCHAR(256) NULL, ticket_url_path NVARCHAR(256) NULL,
          timeout_seconds INT NOT NULL DEFAULT 15,
          max_attempts INT NOT NULL DEFAULT 6,
          rate_limit_per_hour INT NOT NULL DEFAULT 60,
          is_enabled BIT NOT NULL,
          consecutive_failures INT NOT NULL DEFAULT 0,
          circuit_open_until_utc BIGINT NULL,
          created_utc BIGINT NOT NULL
        );

        CREATE TABLE [{schema}].[rules] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, name NVARCHAR(256) NOT NULL,
          app_id NVARCHAR(64) NULL,
          filter NVARCHAR(MAX) NULL, minimum_level INT NOT NULL,
          trigger_type INT NOT NULL,
          threshold_count INT NULL, threshold_window_minutes INT NULL,
          dedupe_key_template NVARCHAR(512) NOT NULL DEFAULT '{DedupeKeyDefault}',
          cooldown_minutes INT NOT NULL DEFAULT 60,
          destination_id NVARCHAR(64) NOT NULL REFERENCES [{schema}].[destinations](id),
          body_template_override NVARCHAR(MAX) NULL,
          max_fires_per_hour INT NOT NULL DEFAULT 20,
          is_dry_run BIT NOT NULL DEFAULT 0,
          is_enabled BIT NOT NULL,
          auto_disabled_reason NVARCHAR(512) NULL,
          last_fired_utc BIGINT NULL, created_utc BIGINT NOT NULL
        );

        CREATE TABLE [{schema}].[rule_occurrences] (
          rule_id NVARCHAR(64) NOT NULL, dedupe_key NVARCHAR(400) NOT NULL,
          window_start_utc BIGINT NOT NULL, [count] INT NOT NULL,
          first_seen_utc BIGINT NOT NULL, last_seen_utc BIGINT NOT NULL,
          sample_event NVARCHAR(MAX) NULL,
          last_fired_utc BIGINT NULL, ticket_url NVARCHAR(2048) NULL,
          PRIMARY KEY (rule_id, dedupe_key)
        );

        CREATE TABLE [{schema}].[deliveries] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, destination_id NVARCHAR(64) NOT NULL,
          rule_id NVARCHAR(64) NULL, app_id NVARCHAR(64) NULL,
          source INT NOT NULL,
          created_utc BIGINT NOT NULL, attempt INT NOT NULL DEFAULT 0,
          next_attempt_utc BIGINT NULL, status INT NOT NULL,
          request_body NVARCHAR(MAX) NULL, response_status INT NULL,
          response_snippet NVARCHAR(MAX) NULL,
          ticket_id NVARCHAR(256) NULL, ticket_url NVARCHAR(2048) NULL, error NVARCHAR(MAX) NULL
        );
        CREATE INDEX ix_deliveries_status ON [{schema}].[deliveries](status, next_attempt_utc);

        CREATE TABLE [{schema}].[ticket_links] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, app_id NVARCHAR(64) NOT NULL, event_type BIGINT NULL,
          dedupe_key NVARCHAR(400) NULL,
          event_id NVARCHAR(64) NULL, ticket_id NVARCHAR(256) NULL, ticket_url NVARCHAR(2048) NULL,
          delivery_id NVARCHAR(64) NULL,
          created_by NVARCHAR(256) NULL, created_utc BIGINT NOT NULL
        );
        CREATE INDEX ix_ticket_links_type ON [{schema}].[ticket_links](app_id, event_type);
        """,

        // v2 — carry the event type on a delivery so its ticket link records it,
        // which powers the "this event type has a ticket" grid badge (SPEC §10.6).
        $"""
        ALTER TABLE [{schema}].[deliveries] ADD event_type BIGINT NULL;
        """,

        // v3 — browser CORS origins allowed to post logs, managed from the admin UI.
        $"""
        CREATE TABLE [{schema}].[cors_origins] (
          origin NVARCHAR(400) NOT NULL PRIMARY KEY,
          created_utc BIGINT NOT NULL
        );
        """,

        // v4 — named, shareable saved searches per app (SPEC §7 explore).
        $"""
        CREATE TABLE [{schema}].[saved_searches] (
          id NVARCHAR(64) NOT NULL PRIMARY KEY, app_id NVARCHAR(64) NOT NULL, name NVARCHAR(256) NOT NULL,
          query NVARCHAR(MAX) NOT NULL, created_by NVARCHAR(256) NULL, created_utc BIGINT NOT NULL
        );
        CREATE INDEX ix_saved_searches_app ON [{schema}].[saved_searches](app_id, name);
        """,

        // v5 — SMTP email destinations: a delivery kind plus SMTP transport config on the
        // destination, and a rendered subject on the delivery (SPEC §10.1).
        $"""
        ALTER TABLE [{schema}].[destinations] ADD
          kind INT NOT NULL DEFAULT 0,
          smtp_host NVARCHAR(256) NULL,
          smtp_port INT NOT NULL DEFAULT 587,
          smtp_security INT NOT NULL DEFAULT 1,
          smtp_username NVARCHAR(256) NULL,
          smtp_from NVARCHAR(256) NULL,
          smtp_to NVARCHAR(MAX) NULL,
          smtp_subject NVARCHAR(512) NOT NULL DEFAULT '{SmtpSubjectDefault}';
        ALTER TABLE [{schema}].[deliveries] ADD subject NVARCHAR(512) NULL;
        """,

        // v6 — error acknowledgements: a watermark per scope so a handled error stops
        // raising the overview alert, while a later occurrence raises it again (SPEC §7).
        $"""
        CREATE TABLE [{schema}].[acks] (
          app_id     NVARCHAR(64)  NOT NULL,
          scope      NVARCHAR(64)  NOT NULL,  -- '*' = every error in the app, else the event type
          through_ts BIGINT        NOT NULL,  -- unix micros; errors at or before this are handled
          note       NVARCHAR(MAX) NULL,
          acked_by   NVARCHAR(256) NULL,
          created_utc BIGINT NOT NULL,
          PRIMARY KEY (app_id, scope)
        );
        """,

        // v7 - Windows integrated sign-in (SPEC section 11): the Windows identity this account
        // answers to, as the server reports it ('DOMAIN\user'). NULL for password-only accounts.
        // The column carries a case-insensitive collation because Windows account names are
        // case-insensitive, and the index is filtered so the many NULL rows don't collide - two
        // accounts must never claim the same identity.
        $"""
        ALTER TABLE [{schema}].[users]
          ADD windows_account NVARCHAR(256) COLLATE Latin1_General_CI_AS NULL;
        EXEC(N'CREATE UNIQUE INDEX ux_users_windows_account
          ON [{schema}].[users](windows_account) WHERE windows_account IS NOT NULL');
        """,

        // v8 - agentless Windows Event Log collection (SPEC section 6.4). One high-water mark
        // per (machine, channel) so a restart resumes where the collector left off. Keys are
        // written lower-cased by the store, so no special collation is needed here.
        $"""
        CREATE TABLE [{schema}].[winlog_cursors] (
          machine        NVARCHAR(256) NOT NULL,
          channel        NVARCHAR(256) NOT NULL,
          last_record_id BIGINT NOT NULL,
          updated_utc    BIGINT NOT NULL,
          PRIMARY KEY (machine, channel)
        );
        """,
    ];
}
