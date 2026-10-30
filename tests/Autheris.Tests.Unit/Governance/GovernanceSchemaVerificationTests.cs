using System.Globalization;
using System.Runtime.CompilerServices;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Plan D-1, D-4, D-5, section 6 (AC-A2.2 to A2.5) and SEC-94-08: read-only shape verification of the governance schema.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceSchemaVerificationTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in _files)
        {
            foreach (var path in new[] { file, file + "-wal", file + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static GatewayOptions Gateway(string connectionString, GovernanceMigrationMode mode) => new()
    {
        GovernanceDb = new GovernanceDbOptions
        {
            ConnectionString = connectionString,
            SeedDemoData = false,
            Migrations = new GovernanceMigrationOptions { Mode = mode }
        }
    };

    private static string MemoryCs() => $"Data Source=ver_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private string FileCs(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "autheris-shape-" + Guid.NewGuid().ToString("N"), "gov.db");
        _files.Add(path);
        return $"Data Source={path}";
    }

    private static SqliteGovernanceRepository NewRepository(string cs) =>
        GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), Options.Create(Gateway(cs, GovernanceMigrationMode.Apply)));

    private static SqliteGovernanceMigrationRunner NewRunner(
        SqliteGovernanceRepository repository, string cs, GovernanceMigrationMode mode = GovernanceMigrationMode.Apply, ILogger<SqliteGovernanceMigrationRunner>? logger = null) =>
        new(repository, Options.Create(Gateway(cs, mode)), logger);

    private static void Exec(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteGovernanceRepository repository, string sql)
    {
        using var cmd = repository.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string FixturePath([CallerFilePath] string? self = null) =>
        Path.Combine(Path.GetDirectoryName(self)!, "Fixtures", "governance-v1.1.8-preshim.sqlite.sql");

    [Fact]
    public async Task ApplyMode_WrongShape_NoVersionRowRecorded()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "ALTER TABLE CONSENT_COLUMN_RULES DROP COLUMN applicable_scopes");
        Exec(repository, "DROP TABLE autheris_schema_migrations");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());

        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.MissingColumn && d.Table == "CONSENT_COLUMN_RULES" && d.Name == "applicable_scopes");
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'autheris_schema_migrations'").ShouldBe(0);
    }

    [Fact]
    public async Task RecordedVersion_WrongShape_FailsOnEveryStart()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "ALTER TABLE CONSENT_COLUMN_RULES DROP COLUMN applicable_scopes");
        Exec(repository, "DROP TABLE TABLE_INDEX_COLUMNS");

        for (var start = 0; start < 2; start++)
        {
            var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());
            ex.Message.ShouldContain("missing column     CONSENT_COLUMN_RULES.applicable_scopes (expected TEXT, family string)");
            ex.Message.ShouldContain("missing table      TABLE_INDEX_COLUMNS");
            ex.Message.ShouldContain("applied migrations: 0001, 0002, 0003, 0004; pending: none");
        }
    }

    [Fact]
    public async Task OldDatabase_WithoutVersionTable_ReportsDriftAndLeavesNoVersionTable()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);
        Exec(repository, File.ReadAllText(FixturePath()));

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());

        // The adoption pre-check rejects the old shape before any DDL runs (I388 check order), so no step is attempted.
        ex.Deviations.ShouldContain(d => d.Kind == SchemaDeviationKind.MissingColumn && d.Table == "AUDIT_LOG_ENTRIES" && d.Name == "seq");
        // Wrong existing objects block; wholly missing tables are not reported by the adoption pre-check (the idempotent baseline creates them).
        ex.Code.ShouldBe(GovernanceMigrationCodes.UnsupportedBase);
        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'autheris_schema_migrations'").ShouldBe(0);
    }

    [Fact]
    public async Task VerifyMode_WrongShape_Throws_AndFreshShapePasses()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        await NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync();

        Exec(repository, "DROP INDEX ux_tables_natural");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync());
        ex.Deviations.ShouldHaveSingleItem().Kind.ShouldBe(SchemaDeviationKind.MissingIndex);
    }

    [Fact]
    public async Task VerifyMode_ExecutesNoDdlOrWrite()
    {
        var cs = FileCs(out var path);
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        await using var observer = new SqliteConnection(cs);
        await observer.OpenAsync();
        long DataVersion()
        {
            using var cmd = observer.CreateCommand();
            cmd.CommandText = "PRAGMA data_version";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        // Control: a write by the runner is visible to the observer (the probe is sensitive).
        var before = DataVersion();
        Exec(repository, "INSERT INTO autheris_schema_migrations (version, name, checksum, applied_at, applied_by) VALUES (99, 'probe', 'x', 'now', 't')");
        DataVersion().ShouldNotBe(before);
        Exec(repository, "DELETE FROM autheris_schema_migrations WHERE version = 99");

        before = DataVersion();
        await NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync();
        await NewRunner(repository, cs, GovernanceMigrationMode.Verify).RunAsync();

        DataVersion().ShouldBe(before);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task DriftMessage_ContainsNoConnectionDetails()
    {
        var cs = FileCs(out var path);
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();
        Exec(repository, "DROP TABLE TABLE_INDEXES");

        var ex = await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs).RunAsync());

        var withoutFooter = ex.Message[..ex.Message.LastIndexOf('\n')];
        withoutFooter.ShouldContain("database 'gov.db'");
        foreach (var forbidden in new[] { "Data Source=", "Password", "Pwd", "User Id", "Host=", "Server=", Path.GetDirectoryName(path)!, "/", "\\" })
        {
            withoutFooter.ShouldNotContain(forbidden, Case.Insensitive);
        }
    }

    [Fact]
    public async Task DriftIsLoggedOnceAtError_AndExtraTablesAreToleratedWithOneInformation()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);
        var logger = new CapturingLogger<SqliteGovernanceMigrationRunner>();
        await NewRunner(repository, cs, logger: logger).RunAsync();
        Exec(repository, "CREATE TABLE MY_EXTRA_TABLE (id TEXT)");
        Exec(repository, "ALTER TABLE TABLES ADD COLUMN my_extra_column TEXT");
        logger.Entries.Clear();

        await NewRunner(repository, cs, logger: logger).RunAsync();

        logger.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("MY_EXTRA_TABLE", StringComparison.Ordinal)).ShouldBe(1);
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);

        Exec(repository, "DROP TABLE TABLE_INDEXES");
        logger.Entries.Clear();
        await Should.ThrowAsync<GovernanceSchemaDriftException>(() => NewRunner(repository, cs, logger: logger).RunAsync());
        logger.Entries.Count(e => e.Level == LogLevel.Error).ShouldBe(1);
    }

    [Fact]
    public async Task SkipMode_DoesNotVerifyTheShape()
    {
        var cs = MemoryCs();
        using var repository = NewRepository(cs);

        await NewRunner(repository, cs, GovernanceMigrationMode.Skip).RunAsync();

        Scalar(repository, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'").ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentRunners_VerifyUnderSchemaLock()
    {
        var cs = FileCs(out _);
        using var repository = NewRepository(cs);
        await NewRunner(repository, cs).RunAsync();

        var runs = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => NewRunner(repository, cs, i % 2 == 0 ? GovernanceMigrationMode.Verify : GovernanceMigrationMode.Apply).RunAsync()))
            .ToArray();

        await Task.WhenAll(runs);
        Scalar(repository, "SELECT COUNT(*) FROM autheris_schema_migrations").ShouldBe(4);
    }
}
