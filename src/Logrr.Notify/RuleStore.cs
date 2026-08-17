using System.Data.Common;
using Logrr.Contracts;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;

namespace Logrr.Notify;

/// <summary>CRUD + state transitions for the <c>rules</c> table (SPEC §10.3).</summary>
public sealed class RuleStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string Table => db.T("rules");

    public void Create(Rule rule) => Write(rule, insert: true);

    public void Update(Rule rule) => Write(rule, insert: false);

    private void Write(Rule x, bool insert)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = insert
            ? $"""
              INSERT INTO {Table} (id, name, app_id, filter, minimum_level, trigger_type,
                threshold_count, threshold_window_minutes, dedupe_key_template, cooldown_minutes,
                destination_id, body_template_override, max_fires_per_hour, is_dry_run, is_enabled,
                auto_disabled_reason, scope_changed_utc, last_fired_utc, created_utc)
              VALUES (@id, @name, @app, @filter, @min, @trig,
                @tc, @tw, @dedupe, @cool, @dest, @override, @maxFires, @dry, @en,
                @reason, @scopeChanged, @lastFired, @created);
              """
            : $"""
              UPDATE {Table} SET name=@name, app_id=@app, filter=@filter, minimum_level=@min,
                trigger_type=@trig, threshold_count=@tc, threshold_window_minutes=@tw,
                dedupe_key_template=@dedupe, cooldown_minutes=@cool, destination_id=@dest,
                body_template_override=@override, max_fires_per_hour=@maxFires, is_dry_run=@dry,
                is_enabled=@en, auto_disabled_reason=@reason, scope_changed_utc=@scopeChanged
              WHERE id=@id;
              """;
        cmd.P("@id", x.Id);
        cmd.P("@name", x.Name);
        cmd.P("@app", x.AppId);
        cmd.P("@filter", x.Filter);
        cmd.P("@min", (int)x.MinimumLevel);
        cmd.P("@trig", (int)x.TriggerType);
        cmd.P("@tc", x.ThresholdCount);
        cmd.P("@tw", x.ThresholdWindowMinutes);
        cmd.P("@dedupe", x.DedupeKeyTemplate);
        cmd.P("@cool", x.CooldownMinutes);
        cmd.P("@dest", x.DestinationId);
        cmd.P("@override", x.BodyTemplateOverride);
        cmd.P("@maxFires", x.MaxFiresPerHour);
        cmd.P("@dry", x.IsDryRun ? 1 : 0);
        cmd.P("@en", x.IsEnabled ? 1 : 0);
        cmd.P("@reason", x.AutoDisabledReason);
        cmd.P("@scopeChanged", x.ScopeChangedUtc.Ms());
        if (insert)
        {
            cmd.P("@lastFired", x.LastFiredUtc.Ms());
            cmd.P("@created", x.CreatedUtc.Ms());
        }
        cmd.ExecuteNonQuery();
    }

    public Rule? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} WHERE id = @id;";
        cmd.P("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<Rule> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} ORDER BY name;";
        using var r = cmd.ExecuteReader();
        var list = new List<Rule>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    /// <summary>Enabled rules that apply to an app (its own or all-apps rules).</summary>
    public IReadOnlyList<Rule> EnabledForApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // is_enabled is BIT on SQL Server and INTEGER on SQLite; "= 1" is valid against both.
        cmd.CommandText = $"SELECT * FROM {Table} WHERE is_enabled = 1 AND (app_id IS NULL OR app_id = @app);";
        cmd.P("@app", appId);
        using var r = cmd.ExecuteReader();
        var list = new List<Rule>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    public void SetLastFired(string id, DateTimeOffset now)
    {
        Exec($"UPDATE {Table} SET last_fired_utc = @now WHERE id = @id;", ("@now", now.Ms()), ("@id", id));
    }

    public void SetEnabled(string id, bool enabled)
    {
        Exec($"UPDATE {Table} SET is_enabled = @en, auto_disabled_reason = NULL WHERE id = @id;",
            ("@en", enabled ? 1 : 0), ("@id", id));
    }

    /// <summary>Auto-disable a rule that blew its hourly fire cap (SPEC §10.7).</summary>
    public void AutoDisable(string id, string reason)
    {
        Exec($"UPDATE {Table} SET is_enabled = 0, auto_disabled_reason = @reason WHERE id = @id;",
            ("@reason", reason), ("@id", id));
    }

    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.P(n, v);
        cmd.ExecuteNonQuery();
    }

    internal static Rule Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        AppId = r.Str("app_id"),
        Filter = r.Str("filter"),
        MinimumLevel = (LogLevel)r.Int32("minimum_level"),
        TriggerType = (TriggerType)r.Int32("trigger_type"),
        ThresholdCount = r.IntNull("threshold_count"),
        ThresholdWindowMinutes = r.IntNull("threshold_window_minutes"),
        DedupeKeyTemplate = r.GetString(r.GetOrdinal("dedupe_key_template")),
        CooldownMinutes = r.Int32("cooldown_minutes"),
        DestinationId = r.GetString(r.GetOrdinal("destination_id")),
        BodyTemplateOverride = r.Str("body_template_override"),
        MaxFiresPerHour = r.Int32("max_fires_per_hour"),
        IsDryRun = r.Bool("is_dry_run"),
        IsEnabled = r.Bool("is_enabled"),
        AutoDisabledReason = r.Str("auto_disabled_reason"),
        ScopeChangedUtc = r.ReadTsNull("scope_changed_utc"),
        LastFiredUtc = r.ReadTsNull("last_fired_utc"),
        CreatedUtc = r.ReadTs("created_utc"),
    };
}
