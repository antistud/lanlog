namespace Logrr.Storage.Sql;

/// <summary>
/// Which database Logrr stores into (SPEC §4.7). <see cref="Sqlite"/> is the default and needs
/// nothing but a writable folder; <see cref="SqlServer"/> is selected by configuring a
/// connection string.
/// </summary>
public enum StorageBackend
{
    /// <summary>Per-app-day SQLite files under the data root, plus <c>control.db</c>.</summary>
    Sqlite = 0,

    /// <summary>A SQL Server database: one table per app-day, plus the control tables.</summary>
    SqlServer = 1,
}
