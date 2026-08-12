using Logrr.Storage.Sql;

namespace Logrr.Storage.Control;

/// <summary>A named, reusable search: the query string that repopulates the Search screen.</summary>
public sealed record SavedSearch(
    string Id, string AppId, string Name, string Query, string? CreatedBy, DateTimeOffset CreatedUtc);

/// <summary>CRUD for the <c>saved_searches</c> table (SPEC §7 explore).</summary>
public sealed class SavedSearchStore(ControlDatabase db)
{
    /// <summary>Schema-qualified on SQL Server, bare on SQLite.</summary>
    private string Table => db.T("saved_searches");

    public IReadOnlyList<SavedSearch> ListByApp(string appId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT id, app_id, name, query, created_by, created_utc FROM {Table} " +
            "WHERE app_id = @a ORDER BY name;";
        cmd.Add("@a", appId);
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedSearch>();
        while (reader.Read())
        {
            list.Add(new SavedSearch(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(reader.GetValue(5)))));
        }
        return list;
    }

    public void Create(SavedSearch s)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {Table} (id, app_id, name, query, created_by, created_utc) " +
            "VALUES (@id, @a, @n, @q, @by, @c);";
        cmd.Add("@id", s.Id);
        cmd.Add("@a", s.AppId);
        cmd.Add("@n", s.Name);
        cmd.Add("@q", s.Query);
        cmd.Add("@by", (object?)s.CreatedBy);
        cmd.Add("@c", s.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table} WHERE id = @id;";
        cmd.Add("@id", id);
        cmd.ExecuteNonQuery();
    }
}
