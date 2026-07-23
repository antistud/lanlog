using Logrr.Contracts;
using Logrr.Core;
using Logrr.Core.Filters;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// The load-bearing invariant of SPEC §7.1: <c>ToSql()</c> and <c>Compile()</c> must agree
/// exactly. We generate a corpus of events, materialise them in a real SQLite table, then
/// for each filter compare the id set returned by the SQL backend against the id set
/// accepted by the in-memory predicate. Any divergence fails the build.
/// </summary>
public class FilterBackendAgreementTests
{
    private static readonly string[] Messages =
    [
        "connection timeout on socket",
        "user login ok",
        "payment processed",
        "healthcheck ping",
        "cache miss for key",
    ];

    private static readonly string?[] Sources = ["Billing", "HealthCheck", "Auth", null];
    private static readonly string[] Regions = ["us", "eu", "apac"];

    private static readonly string[] Filters =
    [
        "Level >= Warning",
        "Level = 4",
        "UserId = 1042",
        "UserId >= 1000 and UserId < 1010",
        "Source = 'HealthCheck'",
        "not Source = 'HealthCheck'",
        "Message like '%timeout%'",
        "Message like '%TIMEOUT%'",
        "Exception is not null",
        "Exception is null",
        "TenantId = 42 or Level >= Error",
        "Source is null",
        "Region = 'eu' and TenantId >= 42",
        "Region like 'u%'",
        "(Level >= Warning and TenantId = 42) or UserId = 1042",
        "not (Source = 'HealthCheck' or Level < Warning)",
        "TenantId is not null and not Region = 'us'",
    ];

    [Fact]
    public void Sql_and_predicate_backends_agree_over_generated_events()
    {
        var events = GenerateEvents(500, seed: 20260723);

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        CreateSchema(conn);
        InsertEvents(conn, events);

        foreach (var filter in Filters)
        {
            var expr = FilterExpression.Parse(filter);

            var sqlIds = QuerySql(conn, expr);
            var predicateIds = PredicateIds(events, expr);

            Assert.True(
                sqlIds.SetEquals(predicateIds),
                $"Backends diverged for filter: {filter}\n" +
                $"  only in SQL:       {string.Join(",", sqlIds.Except(predicateIds).Order())}\n" +
                $"  only in predicate: {string.Join(",", predicateIds.Except(sqlIds).Order())}");
        }
    }

    private static List<(long Id, LogEvent Event)> GenerateEvents(int count, int seed)
    {
        var rnd = new Random(seed);
        var list = new List<(long, LogEvent)>(count);
        for (var i = 0; i < count; i++)
        {
            var props = new Dictionary<string, object?>();
            if (rnd.NextDouble() < 0.70) props["UserId"] = (long)rnd.Next(1000, 1050);
            if (rnd.NextDouble() < 0.60) props["TenantId"] = (long)rnd.Next(40, 46);
            if (rnd.NextDouble() < 0.50) props["Region"] = Regions[rnd.Next(Regions.Length)];

            var e = new LogEvent
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Level = (LogLevel)rnd.Next(0, 6),
                Message = Messages[rnd.Next(Messages.Length)],
                Exception = rnd.NextDouble() < 0.5 ? null : "System.NullReferenceException: obj",
                Source = Sources[rnd.Next(Sources.Length)],
                Properties = props,
            };
            list.Add((i + 1, e)); // rowid is assigned in insertion order starting at 1
        }
        return list;
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE events (
              id INTEGER PRIMARY KEY,
              ts INTEGER NOT NULL,
              level INTEGER NOT NULL,
              message TEXT NOT NULL,
              exception TEXT,
              source TEXT,
              trace_id TEXT,
              span_id TEXT,
              machine TEXT,
              properties TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void InsertEvents(SqliteConnection conn, List<(long Id, LogEvent Event)> events)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events (id, ts, level, message, exception, source, properties)
            VALUES ($id, $ts, $level, $message, $exception, $source, $properties);
            """;
        var pId = cmd.CreateParameter(); pId.ParameterName = "$id"; cmd.Parameters.Add(pId);
        var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
        var pLevel = cmd.CreateParameter(); pLevel.ParameterName = "$level"; cmd.Parameters.Add(pLevel);
        var pMsg = cmd.CreateParameter(); pMsg.ParameterName = "$message"; cmd.Parameters.Add(pMsg);
        var pExc = cmd.CreateParameter(); pExc.ParameterName = "$exception"; cmd.Parameters.Add(pExc);
        var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "$source"; cmd.Parameters.Add(pSrc);
        var pProps = cmd.CreateParameter(); pProps.ParameterName = "$properties"; cmd.Parameters.Add(pProps);

        foreach (var (id, e) in events)
        {
            pId.Value = id;
            pTs.Value = e.Timestamp.ToUnixTimeMilliseconds() * 1000;
            pLevel.Value = (int)e.Level;
            pMsg.Value = e.Message;
            pExc.Value = (object?)e.Exception ?? DBNull.Value;
            pSrc.Value = (object?)e.Source ?? DBNull.Value;
            pProps.Value = PropertyValue.ToJson(e.Properties);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static HashSet<long> QuerySql(SqliteConnection conn, FilterExpression expr)
    {
        var (sql, parameters) = expr.ToSql();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id FROM events WHERE {sql};";
        for (var i = 0; i < parameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@p" + i;
            p.Value = parameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        var ids = new HashSet<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static HashSet<long> PredicateIds(List<(long Id, LogEvent Event)> events, FilterExpression expr)
    {
        var predicate = expr.Compile();
        var ids = new HashSet<long>();
        foreach (var (id, e) in events)
        {
            if (predicate(e))
            {
                ids.Add(id);
            }
        }
        return ids;
    }
}
