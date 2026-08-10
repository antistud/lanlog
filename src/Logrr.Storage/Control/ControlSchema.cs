namespace Logrr.Storage.Control;

/// <summary>
/// Control database schema (SPEC §4.4, §10). Small and low-traffic, so unlike partitions
/// it is migrated by a versioned runner keyed on <c>PRAGMA user_version</c>.
/// </summary>
public static class ControlSchema
{
    /// <summary>Ordered migrations. Index + 1 is the resulting <c>user_version</c>.</summary>
    public static readonly IReadOnlyList<string> Migrations =
    [
        // v1 — initial schema.
        """
        CREATE TABLE apps (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT,
          retention_days INTEGER NOT NULL, max_size_mb INTEGER NOT NULL,
          minimum_level INTEGER NOT NULL, indexed_properties TEXT,
          is_enabled INTEGER NOT NULL, created_utc INTEGER NOT NULL
        );

        CREATE TABLE tokens (
          id TEXT PRIMARY KEY, app_id TEXT NOT NULL REFERENCES apps(id),
          name TEXT, prefix TEXT NOT NULL UNIQUE, hash BLOB NOT NULL,
          scopes INTEGER NOT NULL, expires_utc INTEGER, last_used_utc INTEGER,
          revoked_utc INTEGER, created_utc INTEGER NOT NULL
        );

        CREATE TABLE users (
          id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE,
          password_hash BLOB, password_salt BLOB, role INTEGER NOT NULL,
          must_change_password INTEGER NOT NULL, app_access TEXT, created_utc INTEGER NOT NULL
        );

        CREATE TABLE destinations (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL,
          method TEXT NOT NULL DEFAULT 'POST', content_type TEXT NOT NULL DEFAULT 'application/json',
          headers TEXT,
          auth_mode INTEGER NOT NULL, auth_secret BLOB, auth_header_name TEXT,
          body_template TEXT NOT NULL,
          ticket_id_path TEXT, ticket_url_path TEXT,
          timeout_seconds INTEGER NOT NULL DEFAULT 15,
          max_attempts INTEGER NOT NULL DEFAULT 6,
          rate_limit_per_hour INTEGER NOT NULL DEFAULT 60,
          is_enabled INTEGER NOT NULL,
          consecutive_failures INTEGER NOT NULL DEFAULT 0,
          circuit_open_until_utc INTEGER,
          created_utc INTEGER NOT NULL
        );

        CREATE TABLE rules (
          id TEXT PRIMARY KEY, name TEXT NOT NULL,
          app_id TEXT,
          filter TEXT, minimum_level INTEGER NOT NULL,
          trigger_type INTEGER NOT NULL,
          threshold_count INTEGER, threshold_window_minutes INTEGER,
          dedupe_key_template TEXT NOT NULL DEFAULT '{{event.eventType}}',
          cooldown_minutes INTEGER NOT NULL DEFAULT 60,
          destination_id TEXT NOT NULL REFERENCES destinations(id),
          body_template_override TEXT,
          max_fires_per_hour INTEGER NOT NULL DEFAULT 20,
          is_dry_run INTEGER NOT NULL DEFAULT 0,
          is_enabled INTEGER NOT NULL,
          auto_disabled_reason TEXT,
          last_fired_utc INTEGER, created_utc INTEGER NOT NULL
        );

        CREATE TABLE rule_occurrences (
          rule_id TEXT NOT NULL, dedupe_key TEXT NOT NULL,
          window_start_utc INTEGER NOT NULL, count INTEGER NOT NULL,
          first_seen_utc INTEGER NOT NULL, last_seen_utc INTEGER NOT NULL,
          sample_event TEXT,
          last_fired_utc INTEGER, ticket_url TEXT,
          PRIMARY KEY (rule_id, dedupe_key)
        );

        CREATE TABLE deliveries (
          id TEXT PRIMARY KEY, destination_id TEXT NOT NULL, rule_id TEXT, app_id TEXT,
          source INTEGER NOT NULL,
          created_utc INTEGER NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
          next_attempt_utc INTEGER, status INTEGER NOT NULL,
          request_body TEXT, response_status INTEGER, response_snippet TEXT,
          ticket_id TEXT, ticket_url TEXT, error TEXT
        );
        CREATE INDEX ix_deliveries_status ON deliveries(status, next_attempt_utc);

        CREATE TABLE ticket_links (
          id TEXT PRIMARY KEY, app_id TEXT NOT NULL, event_type INTEGER, dedupe_key TEXT,
          event_id TEXT, ticket_id TEXT, ticket_url TEXT, delivery_id TEXT,
          created_by TEXT, created_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_ticket_links_type ON ticket_links(app_id, event_type);
        """,

        // v2 — carry the event type on a delivery so its ticket link records it,
        // which powers the "this event type has a ticket" grid badge (SPEC §10.6).
        """
        ALTER TABLE deliveries ADD COLUMN event_type INTEGER;
        """,

        // v3 — browser CORS origins allowed to post logs, managed from the admin UI.
        """
        CREATE TABLE cors_origins (
          origin TEXT PRIMARY KEY,
          created_utc INTEGER NOT NULL
        );
        """,

        // v4 — named, shareable saved searches per app (SPEC §7 explore).
        """
        CREATE TABLE saved_searches (
          id TEXT PRIMARY KEY, app_id TEXT NOT NULL, name TEXT NOT NULL,
          query TEXT NOT NULL, created_by TEXT, created_utc INTEGER NOT NULL
        );
        CREATE INDEX ix_saved_searches_app ON saved_searches(app_id, name);
        """,

        // v5 — SMTP email destinations: a delivery kind plus SMTP transport config on the
        // destination, and a rendered subject on the delivery (SPEC §10.1).
        """
        ALTER TABLE destinations ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE destinations ADD COLUMN smtp_host TEXT;
        ALTER TABLE destinations ADD COLUMN smtp_port INTEGER NOT NULL DEFAULT 587;
        ALTER TABLE destinations ADD COLUMN smtp_security INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE destinations ADD COLUMN smtp_username TEXT;
        ALTER TABLE destinations ADD COLUMN smtp_from TEXT;
        ALTER TABLE destinations ADD COLUMN smtp_to TEXT;
        ALTER TABLE destinations ADD COLUMN smtp_subject TEXT NOT NULL DEFAULT '[{{app.name}}] {{event.level}}: {{event.message}}';
        ALTER TABLE deliveries ADD COLUMN subject TEXT;
        """,

        // v6 — error acknowledgements: a watermark per scope so a handled error stops
        // raising the overview alert, while a later occurrence raises it again (SPEC §7).
        """
        CREATE TABLE acks (
          app_id     TEXT NOT NULL,
          scope      TEXT NOT NULL,     -- '*' = every error in the app, else the event type
          through_ts INTEGER NOT NULL,  -- unix micros; errors at or before this are handled
          note       TEXT,
          acked_by   TEXT,
          created_utc INTEGER NOT NULL,
          PRIMARY KEY (app_id, scope)
        );
        """,

        // v7 - Windows integrated sign-in (SPEC section 11): the Windows identity this account
        // answers to, as the server reports it ('DOMAIN\user'). NULL for password-only accounts.
        // The index is NOCASE because Windows account names are case-insensitive, and partial so
        // the many NULL rows don't collide - two accounts must never claim the same identity.
        """
        ALTER TABLE users ADD COLUMN windows_account TEXT;
        CREATE UNIQUE INDEX ux_users_windows_account
          ON users(windows_account COLLATE NOCASE) WHERE windows_account IS NOT NULL;
        """,

        // v8 - agentless Windows Event Log collection (SPEC section 6.4). One high-water mark
        // per (machine, channel) so a restart resumes where the collector left off instead of
        // re-shipping or skipping. EventRecordID is monotonic within a channel and resets to 1
        // when the log is cleared, which the collector detects and recovers from.
        """
        CREATE TABLE winlog_cursors (
          machine        TEXT NOT NULL,   -- as configured, lower-cased
          channel        TEXT NOT NULL,   -- log name, e.g. 'Application'
          last_record_id INTEGER NOT NULL,
          updated_utc    INTEGER NOT NULL,
          PRIMARY KEY (machine, channel)
        );
        """,
    ];
}
