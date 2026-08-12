using System.Data.Common;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>The collector's global knobs — the single row of <c>winlog_settings</c>.</summary>
public sealed record WinlogSettings
{
    public bool Enabled { get; init; }
    public int PollIntervalSeconds { get; init; } = 60;
    public int MaxEventsPerPoll { get; init; } = 500;
    public int MaxBatchesPerPoll { get; init; } = 10;
    public int InitialBackfillHours { get; init; }
}

/// <summary>A row of <c>winlog_sources</c>: one collected machine and the channels to read.</summary>
public sealed record WinlogSource
{
    public required string Id { get; init; }

    /// <summary>NetBIOS name, FQDN, or <c>.</c> for the Logrr host itself. Unique across rows.</summary>
    public required string Machine { get; init; }

    /// <summary>Logrr app the events land in. Auto-created on first poll; must be a slug.</summary>
    public required string AppId { get; init; }

    /// <summary>Display name used only when auto-creating the app.</summary>
    public string? AppName { get; init; }

    /// <summary>Log names to read. Empty means the collector's default pair.</summary>
    public IReadOnlyList<string> Channels { get; init; } = [];

    /// <summary>Seeds the app's floor on creation only; afterwards the app's own setting governs.</summary>
    public LogLevel? MinimumLevel { get; init; }

    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>
/// CRUD for the agentless Windows Event Log collector's configuration (SPEC §6.4): the global
/// settings row plus one row per collected machine. This is runtime-changeable state, so it
/// lives here rather than in <c>appsettings.json</c> — the config section only seeds it on the
/// first run that finds these tables empty.
/// </summary>
public sealed class WinlogConfigStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string SettingsTable => db.T("winlog_settings");
    private string SourcesTable => db.T("winlog_sources");

    /// <summary>The settings row, or null before anything has been saved or seeded.</summary>
    public WinlogSettings? GetSettings()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT enabled, poll_interval_seconds, max_events_per_poll,
                   max_batches_per_poll, initial_backfill_hours
            FROM {SettingsTable} WHERE id = 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new WinlogSettings
        {
            Enabled = reader.Bool("enabled"),
            PollIntervalSeconds = reader.Int32("poll_interval_seconds"),
            MaxEventsPerPoll = reader.Int32("max_events_per_poll"),
            MaxBatchesPerPoll = reader.Int32("max_batches_per_poll"),
            InitialBackfillHours = reader.Int32("initial_backfill_hours"),
        };
    }

    public void SaveSettings(WinlogSettings s, DateTimeOffset now)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // Single row, written only from the admin UI, so update-then-insert is upsert enough.
        cmd.CommandText = db.Dialect.IsSqlServer
            ? $"""
              UPDATE {SettingsTable} SET enabled = @en, poll_interval_seconds = @poll,
                max_events_per_poll = @events, max_batches_per_poll = @batches,
                initial_backfill_hours = @backfill, updated_utc = @u
              WHERE id = 1;
              IF @@ROWCOUNT = 0
                INSERT INTO {SettingsTable} (id, enabled, poll_interval_seconds, max_events_per_poll,
                  max_batches_per_poll, initial_backfill_hours, updated_utc)
                VALUES (1, @en, @poll, @events, @batches, @backfill, @u);
              """
            : $"""
              INSERT INTO {SettingsTable} (id, enabled, poll_interval_seconds, max_events_per_poll,
                max_batches_per_poll, initial_backfill_hours, updated_utc)
              VALUES (1, @en, @poll, @events, @batches, @backfill, @u)
              ON CONFLICT(id) DO UPDATE SET
                enabled = excluded.enabled,
                poll_interval_seconds = excluded.poll_interval_seconds,
                max_events_per_poll = excluded.max_events_per_poll,
                max_batches_per_poll = excluded.max_batches_per_poll,
                initial_backfill_hours = excluded.initial_backfill_hours,
                updated_utc = excluded.updated_utc;
              """;
        cmd.Add("@en", s.Enabled ? 1 : 0);
        cmd.Add("@poll", s.PollIntervalSeconds);
        cmd.Add("@events", s.MaxEventsPerPoll);
        cmd.Add("@batches", s.MaxBatchesPerPoll);
        cmd.Add("@backfill", s.InitialBackfillHours);
        cmd.Add("@u", now.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<WinlogSource> ListSources()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {SourcesTable} ORDER BY machine;";
        using var reader = cmd.ExecuteReader();
        var list = new List<WinlogSource>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public WinlogSource? GetSource(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {SourcesTable} WHERE id = @id;";
        cmd.Add("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Another row already collecting this machine, if any. Ignores <paramref name="exceptId"/>.</summary>
    public bool MachineTaken(string machine, string? exceptId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // Compared here rather than in SQL so the answer does not depend on the column's
        // collation: SQLite would need COLLATE NOCASE and SQL Server carries it on the column.
        cmd.CommandText = $"SELECT id, machine FROM {SourcesTable};";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0) != exceptId &&
                string.Equals(reader.GetString(1), machine.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public void CreateSource(WinlogSource s)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {SourcesTable} (id, machine, app_id, app_name, channels,
              minimum_level, is_enabled, created_utc)
            VALUES (@id, @m, @app, @name, @ch, @min, @en, @created);
            """;
        Bind(cmd, s);
        cmd.Add("@created", s.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void UpdateSource(WinlogSource s)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {SourcesTable} SET machine = @m, app_id = @app, app_name = @name,
              channels = @ch, minimum_level = @min, is_enabled = @en
            WHERE id = @id;
            """;
        Bind(cmd, s);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSource(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SourcesTable} WHERE id = @id;";
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(DbCommand cmd, WinlogSource s)
    {
        cmd.Add("@id", s.Id);
        cmd.Add("@m", s.Machine);
        cmd.Add("@app", s.AppId);
        cmd.Add("@name", (object?)s.AppName);
        cmd.Add("@ch", JsonSerializer.Serialize(s.Channels));
        cmd.Add("@min", s.MinimumLevel is { } level ? (int)level : (object?)null);
        cmd.Add("@en", s.IsEnabled ? 1 : 0);
    }

    private static WinlogSource Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Machine = r.GetString(r.GetOrdinal("machine")),
        AppId = r.GetString(r.GetOrdinal("app_id")),
        AppName = r.Str("app_name"),
        Channels = AppStore.ReadStringList(r, "channels"),
        MinimumLevel = r.IntNull("minimum_level") is { } level ? (LogLevel)level : null,
        IsEnabled = r.Bool("is_enabled"),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.Int64("created_utc")),
    };
}
