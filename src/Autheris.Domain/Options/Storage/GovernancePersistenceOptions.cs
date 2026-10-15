using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class GovernanceDbOptions
{
    public string Provider { get; init; } = "Sqlite"; // "Sqlite", "PostgreSql" ("Postgres", "PgSql") or "SqlServer" ("MsSql")
    /// <summary>
    /// ADO.NET connection string. Empty (default) for SQLite: Development/Test use a private in-memory database, every other environment
    /// the governance database file in <c>Gateway:State:StateDirectory</c> (WP-ARC-B-13).
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;
    [Range(1, 60)] public int CommandTimeoutSeconds { get; init; } = 15;
    public bool EnableOutboxProcessor { get; init; } = true;
    public bool? SeedDemoData { get; init; } = null;
    public string? AuditHmacKeyVaultRef { get; init; }

    /// <summary>
    /// Review PG-7: optional connection string of a separate role that applies the schema (DDL) at startup (PostgreSQL / SQL Server). When set,
    /// the runtime connection string only needs DML rights (INSERT/SELECT on AUDIT_LOG_ENTRIES), so the runtime account cannot
    /// alter or truncate the audit table.
    /// </summary>
    public string? MigrationConnectionString { get; init; }

    /// <summary>
    /// R-42: SQLite journal mode: WAL (default), DELETE, TRUNCATE, MEMORY, OFF.
    /// When running inside Docker/Podman with bind mounts from Windows/WSL filesystems where
    /// shared memory (-shm) is broken, configure DELETE or TRUNCATE to avoid database disk image corruption.
    /// </summary>
    public string JournalMode { get; init; } = "WAL";

    /// <summary>WP-ARC-B-12: versioned governance schema migrations (<c>GovernanceDb:Migrations</c>).</summary>
    public GovernanceMigrationOptions Migrations { get; init; } = new();
}

/// <summary>How the governance schema migrations are handled at startup.</summary>
public enum GovernanceMigrationMode
{
    /// <summary>Apply pending migrations under the schema lock (default). Startup fails when a migration fails.</summary>
    Apply = 0,

    /// <summary>Do not change the schema; startup fails when migrations are pending (a separate pipeline migrates).</summary>
    Verify = 1,

    /// <summary>Do not inspect or change the schema. Rejected outside Development/Test.</summary>
    Skip = 2
}

public sealed class GovernanceMigrationOptions
{
    public GovernanceMigrationMode Mode { get; init; } = GovernanceMigrationMode.Apply;

    /// <summary>
    /// When false (default) startup fails if the database contains migrations newer than this binary (database newer than code).
    /// </summary>
    public bool AllowNewerSchema { get; init; }
}
