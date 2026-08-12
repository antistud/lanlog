using System.Data.Common;
using System.Text.Json;
using Logrr.Contracts;
using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>CRUD for the <c>apps</c> table.</summary>
public sealed class AppStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string Table => db.T("apps");

    public void Create(AppRecord app)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {Table} (id, name, description, retention_days, max_size_mb,
                              minimum_level, indexed_properties, is_enabled, created_utc)
            VALUES (@id, @name, @desc, @ret, @max, @min, @idx, @en, @created);
            """;
        cmd.Add("@id", app.Id);
        cmd.Add("@name", app.Name);
        cmd.Add("@desc", app.Description);
        cmd.Add("@ret", app.RetentionDays);
        cmd.Add("@max", app.MaxSizeMb);
        cmd.Add("@min", (int)app.MinimumLevel);
        cmd.Add("@idx", JsonSerializer.Serialize(app.IndexedProperties));
        cmd.Add("@en", app.IsEnabled ? 1 : 0);
        cmd.Add("@created", app.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public AppRecord? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} WHERE id = @id;";
        cmd.Add("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AppRecord> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Table} ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var apps = new List<AppRecord>();
        while (reader.Read())
        {
            apps.Add(Map(reader));
        }
        return apps;
    }

    public void Update(AppRecord app)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {Table} SET name = @name, description = @desc, retention_days = @ret,
              max_size_mb = @max, minimum_level = @min, indexed_properties = @idx,
              is_enabled = @en
            WHERE id = @id;
            """;
        cmd.Add("@id", app.Id);
        cmd.Add("@name", app.Name);
        cmd.Add("@desc", app.Description);
        cmd.Add("@ret", app.RetentionDays);
        cmd.Add("@max", app.MaxSizeMb);
        cmd.Add("@min", (int)app.MinimumLevel);
        cmd.Add("@idx", JsonSerializer.Serialize(app.IndexedProperties));
        cmd.Add("@en", app.IsEnabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    internal static AppRecord Map(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Description = r.Str("description"),
        RetentionDays = r.Int32("retention_days"),
        MaxSizeMb = r.Int32("max_size_mb"),
        MinimumLevel = (LogLevel)r.Int32("minimum_level"),
        IndexedProperties = ReadStringList(r, "indexed_properties"),
        IsEnabled = r.Bool("is_enabled"),
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.Int64("created_utc")),
    };

    internal static IReadOnlyList<string> ReadStringList(DbDataReader r, string column)
    {
        var json = r.Str(column);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
