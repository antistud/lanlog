using System.Text;
using System.Text.RegularExpressions;

namespace Logrr.Storage;

/// <summary>
/// The per-partition DDL (SPEC §4.3). Partitions are created from a known DDL string, not
/// migrated — the schema is fixed and the write path is hot.
/// </summary>
public static partial class PartitionSchema
{
    public const string BaseDdl = """
        CREATE TABLE IF NOT EXISTS events (
          id          INTEGER PRIMARY KEY,
          ts          INTEGER NOT NULL,
          level       INTEGER NOT NULL,
          template    TEXT,
          message     TEXT NOT NULL,
          exception   TEXT,
          event_type  INTEGER,
          trace_id    TEXT,
          span_id     TEXT,
          source      TEXT,
          machine     TEXT,
          properties  TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_events_ts       ON events(ts DESC);
        CREATE INDEX IF NOT EXISTS ix_events_level_ts ON events(level, ts DESC);
        CREATE INDEX IF NOT EXISTS ix_events_type_ts  ON events(event_type, ts DESC);
        CREATE INDEX IF NOT EXISTS ix_events_trace    ON events(trace_id) WHERE trace_id IS NOT NULL;
        CREATE VIRTUAL TABLE IF NOT EXISTS events_fts USING fts5(
          message, exception,
          content = 'events', content_rowid = 'id',
          tokenize = 'porter unicode61'
        );
        """;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SafeName();

    /// <summary>
    /// Build the full DDL for a new partition, adding an expression index per indexed
    /// property from the app's config snapshot (SPEC §4.3). Applied at partition creation;
    /// changing the list affects tomorrow's partition.
    /// </summary>
    public static string BuildDdl(IEnumerable<string> indexedProperties)
    {
        var sb = new StringBuilder(BaseDdl);
        sb.AppendLine();
        foreach (var prop in indexedProperties)
        {
            if (!SafeName().IsMatch(prop))
            {
                continue; // ignore anything that isn't a safe identifier
            }
            sb.AppendLine(
                $"CREATE INDEX IF NOT EXISTS ix_events_prop_{prop} " +
                $"ON events(json_extract(properties, '$.{prop}'));");
        }
        return sb.ToString();
    }
}
