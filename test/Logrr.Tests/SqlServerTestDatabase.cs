using Microsoft.Data.SqlClient;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// A throwaway SQL Server database for the backend tests. Points at whatever
/// <c>LOGRR_TEST_SQL_CONNECTION</c> names, and otherwise at LocalDB, which is present on any
/// machine with Visual Studio or the SQL Server tooling installed.
/// </summary>
/// <remarks>
/// These tests exercise real T-SQL — <c>OPENJSON</c>, <c>TRY_CAST</c>, filtered indexes,
/// persisted computed columns, <c>OFFSET/FETCH</c> — none of which SQLite can stand in for.
/// When no server is reachable the tests report as skipped rather than passing vacuously, so a
/// run on a machine without SQL Server never claims the backend was verified.
/// </remarks>
public sealed class SqlServerTestDatabase : IDisposable
{
    private const string DefaultServer =
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;";

    private static readonly Lazy<string?> ServerConnection = new(Probe);

    private readonly string _databaseName;

    public SqlServerTestDatabase()
    {
        if (ServerConnection.Value is not { } server)
        {
            throw new InvalidOperationException("No SQL Server available.");
        }

        _databaseName = "logrr_test_" + Guid.NewGuid().ToString("N")[..12];
        using (var conn = new SqlConnection(server))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{_databaseName}];";
            cmd.ExecuteNonQuery();
        }

        ConnectionString = new SqlConnectionStringBuilder(server)
        {
            InitialCatalog = _databaseName,
        }.ToString();
    }

    public string ConnectionString { get; }

    /// <summary>Whether a server could be reached, evaluated once per test run.</summary>
    public static bool IsAvailable => ServerConnection.Value is not null;

    public static string SkipReason =>
        "No SQL Server reachable. Install SQL Server LocalDB or set LOGRR_TEST_SQL_CONNECTION.";

    private static string? Probe()
    {
        var candidate = Environment.GetEnvironmentVariable("LOGRR_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = DefaultServer;
        }

        try
        {
            using var conn = new SqlConnection(candidate);
            conn.Open();
            return candidate;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (ServerConnection.Value is not { } server)
        {
            return;
        }

        // Pooled connections keep the database in use, so they have to go before the drop; the
        // single-user flip evicts anything else still attached.
        SqlConnection.ClearAllPools();
        try
        {
            using var conn = new SqlConnection(server);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{_databaseName}];";
            cmd.ExecuteNonQuery();
        }
        catch (SqlException)
        {
            // A leaked test database is noise, not a failure.
        }
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips itself when no SQL Server is reachable.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerTestDatabase.IsAvailable)
        {
            Skip = SqlServerTestDatabase.SkipReason;
        }
    }
}
