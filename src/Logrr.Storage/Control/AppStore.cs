using System.Text.Json;
using Logrr.Contracts;
using Microsoft.Data.Sqlite;

namespace Logrr.Storage.Control;

/// <summary>CRUD for the <c>apps</c> table.</summary>
public sealed class AppStore(ControlDatabase db)
{
    public void Create(AppRecord app)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO apps (id, name, description, retention_days, max_size_mb,
                              minimum_level, indexed_properties, is_enabled, created_utc)
            VALUES ($id, $name, $desc, $ret, $max, $min, $idx, $en, $created);
            """;
        cmd.Add("$id", app.Id);
        cmd.Add("$name", app.Name);
        cmd.Add("$desc", app.Description);
        cmd.Add("$ret", app.RetentionDays);
        cmd.Add("$max", app.MaxSizeMb);
        cmd.Add("$min", (int)app.MinimumLevel);
        cmd.Add("$idx", JsonSerializer.Serialize(app.IndexedProperties));
        cmd.Add("$en", app.IsEnabled ? 1 : 0);
        cmd.Add("$created", app.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public AppRecord? Get(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM apps WHERE id = $id;";
        cmd.Add("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AppRecord> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM apps ORDER BY name;";
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
        cmd.CommandText = """
            UPDATE apps SET name = $name, description = $desc, retention_days = $ret,
              max_size_mb = $max, minimum_level = $min, indexed_properties = $idx,
              is_enabled = $en
            WHERE id = $id;
            """;
        cmd.Add("$id", app.Id);
        cmd.Add("$name", app.Name);
        cmd.Add("$desc", app.Description);
        cmd.Add("$ret", app.RetentionDays);
        cmd.Add("$max", app.MaxSizeMb);
        cmd.Add("$min", (int)app.MinimumLevel);
        cmd.Add("$idx", JsonSerializer.Serialize(app.IndexedProperties));
        cmd.Add("$en", app.IsEnabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    internal static AppRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Description = r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
        RetentionDays = r.GetInt32(r.GetOrdinal("retention_days")),
        MaxSizeMb = r.GetInt32(r.GetOrdinal("max_size_mb")),
        MinimumLevel = (LogLevel)r.GetInt32(r.GetOrdinal("minimum_level")),
        IndexedProperties = ReadStringList(r, "indexed_properties"),
        IsEnabled = r.GetInt32(r.GetOrdinal("is_enabled")) != 0,
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_utc"))),
    };

    internal static IReadOnlyList<string> ReadStringList(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        if (r.IsDBNull(ord))
        {
            return [];
        }
        var json = r.GetString(ord);
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
