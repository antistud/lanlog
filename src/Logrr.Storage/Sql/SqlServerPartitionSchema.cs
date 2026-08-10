using System.Text;
using System.Text.RegularExpressions;

namespace Logrr.Storage.Sql;

/// <summary>
/// The per-partition DDL for SQL Server — the counterpart of <see cref="PartitionSchema"/>
/// (SPEC §4.3, §4.7). One table per app-day, created on first write and never migrated.
/// </summary>
public static partial class SqlServerPartitionSchema
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeName();

    /// <summary>
    /// Idempotent DDL creating an app-day's table and its indexes, including one persisted
    /// computed column plus index per indexed property.
    /// </summary>
    /// <remarks>
    /// Two departures from the SQLite schema, both forced by the platform:
    /// <list type="bullet">
    /// <item><c>id</c> is a plain <c>BIGINT</c> key, not an identity column. The single batch
    /// writer already assigns contiguous ids, and doing it explicitly keeps the composite event
    /// id (<c>{yyyyMMdd}:{rowid}</c>) and cursor paging identical across both backends.</item>
    /// <item>There is no full-text index. SQL Server full-text needs a catalog per table and
    /// populates asynchronously, so a just-written event would not be findable; text search
    /// runs as a <c>LIKE</c> scan inside the day's partition instead.</item>
    /// </list>
    /// Every statement is wrapped in <c>EXEC</c> so it runs as its own batch. Without that,
    /// <c>CREATE INDEX</c> on a table or column created earlier in the same batch fails to
    /// compile — the object does not exist yet when the batch is parsed.
    /// </remarks>
    public static string BuildDdl(string schema, string table, IEnumerable<string> indexedProperties)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"IF OBJECT_ID('{schema}.{table}') IS NULL");
        sb.AppendLine("BEGIN");
        Batch(sb, $"""
            CREATE TABLE [{schema}].[{table}] (
                id          BIGINT        NOT NULL PRIMARY KEY,
                ts          BIGINT        NOT NULL,
                level       INT           NOT NULL,
                template    NVARCHAR(MAX) NULL,
                message     NVARCHAR(MAX) NOT NULL,
                exception   NVARCHAR(MAX) NULL,
                event_type  BIGINT        NULL,
                trace_id    NVARCHAR(64)  NULL,
                span_id     NVARCHAR(64)  NULL,
                source      NVARCHAR(256) NULL,
                machine     NVARCHAR(256) NULL,
                properties  NVARCHAR(MAX) NULL)
            """);
        Batch(sb, $"CREATE INDEX [ix_{table}_ts] ON [{schema}].[{table}](ts DESC)");
        Batch(sb, $"CREATE INDEX [ix_{table}_level_ts] ON [{schema}].[{table}](level, ts DESC)");
        Batch(sb, $"CREATE INDEX [ix_{table}_type_ts] ON [{schema}].[{table}](event_type, ts DESC)");
        Batch(sb, $"CREATE INDEX [ix_{table}_trace] ON [{schema}].[{table}](trace_id) " +
                  "WHERE trace_id IS NOT NULL");
        sb.AppendLine("END");

        foreach (var prop in indexedProperties)
        {
            if (!SafeName().IsMatch(prop))
            {
                continue; // ignore anything that isn't a safe identifier
            }

            // The CAST down to NVARCHAR(400) is not optional: JSON_VALUE yields NVARCHAR(4000),
            // and an index key that wide exceeds SQL Server's 1700-byte limit — CREATE INDEX
            // only warns, then ingest fails on the first long value. Filters still compare the
            // untruncated JSON_VALUE, so this index is an optimisation the planner may or may
            // not pick up; no query result depends on it.
            sb.AppendLine($"IF COL_LENGTH('{schema}.{table}', 'prop_{prop}') IS NULL");
            sb.AppendLine("BEGIN");
            Batch(sb, $"ALTER TABLE [{schema}].[{table}] ADD [prop_{prop}] AS " +
                      $"CAST(JSON_VALUE(properties, '$.{prop}') AS NVARCHAR(400)) PERSISTED");
            Batch(sb, $"CREATE INDEX [ix_{table}_prop_{prop}] ON [{schema}].[{table}]([prop_{prop}])");
            sb.AppendLine("END");
        }

        return sb.ToString();
    }

    /// <summary>Emit one statement as its own batch, quoting it as a T-SQL string literal.</summary>
    private static void Batch(StringBuilder sb, string statement) =>
        sb.Append("  EXEC(N'").Append(statement.Replace("'", "''")).AppendLine("');");
}
