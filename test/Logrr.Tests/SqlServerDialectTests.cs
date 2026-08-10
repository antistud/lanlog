using Logrr.Storage;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// Properties of the SQL Server dialect that hold without a server attached: schema parity with
/// the SQLite migration list, table naming, and the shape of the generated DDL.
/// </summary>
public class SqlServerDialectTests
{
    /// <summary>
    /// The migration lists are the same length on both backends, because the stored version
    /// number is a single integer shared by both. If someone adds a migration to one list and
    /// forgets the other, a SQL Server deployment silently stops at the older version and the
    /// missing table only surfaces as a runtime error — so fail the build here instead.
    /// </summary>
    [Fact]
    public void Migration_lists_stay_in_lockstep_across_backends()
    {
        Assert.Equal(
            ControlSchema.Migrations.Count,
            SqlServerControlSchema.Migrations("logrr").Count);
    }

    [Fact]
    public void Partition_table_name_is_the_app_slug_and_the_day()
    {
        Assert.Equal(
            "events_billing_20260723",
            SqlServerDialect.PartitionTableName("billing", new DateOnly(2026, 7, 23)));
    }

    /// <summary>
    /// App ids are slugs, so a hyphen is the only character that needs mapping — and since a
    /// slug can never contain an underscore, <c>a-b</c> and <c>a_b</c> cannot collide.
    /// </summary>
    [Fact]
    public void Hyphens_in_app_ids_map_to_underscores()
    {
        Assert.Equal(
            "events_web_api_20260723",
            SqlServerDialect.PartitionTableName("web-api", new DateOnly(2026, 7, 23)));
    }

    [Theory]
    [InlineData("Billing")]           // upper case
    [InlineData("bill;drop")]         // punctuation
    [InlineData("bill_ing")]          // underscore would collide with the hyphen mapping
    [InlineData("")]
    public void App_ids_that_are_not_slugs_are_refused(string appId)
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerDialect.PartitionTableName(appId, new DateOnly(2026, 7, 23)));
    }

    [Fact]
    public void Schema_name_must_be_an_identifier()
    {
        Assert.Throws<ArgumentException>(() => new SqlServerDialect("Server=.;", "logrr];DROP"));
    }

    [Fact]
    public void Partition_ddl_creates_the_table_and_its_indexes_idempotently()
    {
        var ddl = SqlServerPartitionSchema.BuildDdl("logrr", "events_billing_20260723", []);

        Assert.Contains("IF OBJECT_ID('logrr.events_billing_20260723') IS NULL", ddl);
        Assert.Contains("CREATE TABLE [logrr].[events_billing_20260723]", ddl);
        Assert.Contains("ix_events_billing_20260723_level_ts", ddl);
        Assert.Contains("WHERE trace_id IS NOT NULL", ddl);
    }

    /// <summary>
    /// An indexed property becomes a persisted computed column. The <c>NVARCHAR(400)</c> cast is
    /// load-bearing: <c>JSON_VALUE</c> yields <c>NVARCHAR(4000)</c>, which blows SQL Server's
    /// 1700-byte index key limit and fails at insert time rather than at CREATE INDEX.
    /// </summary>
    [Fact]
    public void Indexed_properties_become_persisted_computed_columns()
    {
        var ddl = SqlServerPartitionSchema.BuildDdl("logrr", "events_billing_20260723", ["UserId"]);

        Assert.Contains("[prop_UserId]", ddl);
        Assert.Contains("CAST(JSON_VALUE(properties, ''$.UserId'') AS NVARCHAR(400)) PERSISTED", ddl);
        Assert.Contains("ix_events_billing_20260723_prop_UserId", ddl);
    }

    [Fact]
    public void Unsafe_property_names_are_dropped_from_the_ddl()
    {
        var ddl = SqlServerPartitionSchema.BuildDdl(
            "logrr", "events_billing_20260723", ["ok", "not ok", "x');DROP TABLE t;--"]);

        Assert.Contains("[prop_ok]", ddl);
        Assert.DoesNotContain("DROP TABLE", ddl);
        Assert.DoesNotContain("not ok", ddl);
    }

    /// <summary>
    /// Every DDL statement runs as its own batch. Without that, <c>CREATE INDEX</c> naming a
    /// table or computed column created earlier in the same batch fails to compile.
    /// </summary>
    [Fact]
    public void Every_ddl_statement_is_its_own_batch()
    {
        var ddl = SqlServerPartitionSchema.BuildDdl("logrr", "events_billing_20260723", ["UserId"]);

        foreach (var keyword in new[] { "CREATE TABLE", "CREATE INDEX", "ALTER TABLE" })
        {
            var index = ddl.IndexOf(keyword, StringComparison.Ordinal);
            while (index >= 0)
            {
                var lineStart = ddl.LastIndexOf('\n', index) + 1;
                Assert.Contains("EXEC(N'", ddl[lineStart..index]);
                index = ddl.IndexOf(keyword, index + 1, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Selecting_a_backend_follows_the_connection_string()
    {
        var paths = new StoragePaths(Path.GetTempPath());

        Assert.Equal(StorageBackend.Sqlite,
            SqlDialect.Create(new StorageOptions { DataPath = paths.DataRoot }, paths).Backend);

        Assert.Equal(StorageBackend.SqlServer,
            SqlDialect.Create(
                new StorageOptions { DataPath = paths.DataRoot, ConnectionString = "Server=.;Database=x;" },
                paths).Backend);
    }
}
