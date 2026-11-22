using System.Data.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Autheris.Tests.Integration.Governance;
using Microsoft.Data.SqlClient;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

/// <summary>
/// Step 0036 on PostgreSQL and SQL Server: governance domains, domain table assignments, object protection,
/// access policy settings, governance epochs, active-tenant seeding, mask-category backfill, idempotent re-run, and full shape verification.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceDomainsMigrationStep0036Tests
{
    private const string StepName = "governance_domains_and_protection";
    private const string UnassignedDomainId = "00000000-0000-0000-0000-000000000000";

    private const string PostgreSqlPreShape = """
        CREATE SCHEMA pre_step;
        SET search_path TO pre_step;
        CREATE TABLE tenant_registry (
            normalized_key TEXT PRIMARY KEY,
            tenant_id TEXT NOT NULL,
            collided INTEGER NOT NULL DEFAULT 0,
            first_seen_at TEXT NOT NULL
        );
        CREATE TABLE table_columns (
            id TEXT PRIMARY KEY,
            table_id TEXT NOT NULL,
            column_name TEXT NOT NULL,
            data_type TEXT NOT NULL
        );
        CREATE TABLE data_classifications (
            scope VARCHAR(64) NOT NULL,
            code VARCHAR(64) NOT NULL,
            restriction_rank SMALLINT NOT NULL,
            PRIMARY KEY (scope, code)
        );
        CREATE TABLE column_classifications (
            scope VARCHAR(64) NOT NULL,
            table_column_id TEXT NOT NULL,
            code VARCHAR(64) NOT NULL,
            source VARCHAR(16) NOT NULL,
            assigned_by VARCHAR(256) NOT NULL,
            assigned_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (scope, table_column_id, code)
        );
        """;

    private const string SqlServerPreShape = """
        CREATE TABLE dbo.TENANT_REGISTRY (
            normalized_key NVARCHAR(256) NOT NULL PRIMARY KEY,
            tenant_id NVARCHAR(256) NOT NULL,
            collided INT NOT NULL DEFAULT 0,
            first_seen_at NVARCHAR(64) NOT NULL
        );
        CREATE TABLE dbo.TABLE_COLUMNS (
            id NVARCHAR(128) NOT NULL PRIMARY KEY,
            table_id NVARCHAR(128) NOT NULL,
            column_name NVARCHAR(256) NOT NULL,
            data_type NVARCHAR(256) NOT NULL
        );
        CREATE TABLE dbo.DATA_CLASSIFICATIONS (
            scope NVARCHAR(64) NOT NULL,
            code NVARCHAR(64) NOT NULL,
            restriction_rank SMALLINT NOT NULL,
            CONSTRAINT PK_DATA_CLASSIFICATIONS PRIMARY KEY (scope, code)
        );
        CREATE TABLE dbo.COLUMN_CLASSIFICATIONS (
            scope NVARCHAR(64) NOT NULL,
            table_column_id NVARCHAR(128) NOT NULL,
            code NVARCHAR(64) NOT NULL,
            source NVARCHAR(16) NOT NULL,
            assigned_by NVARCHAR(256) NOT NULL,
            assigned_at DATETIMEOFFSET NOT NULL,
            CONSTRAINT PK_COLUMN_CLASSIFICATIONS PRIMARY KEY (scope, table_column_id, code)
        );
        """;

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Step0036_SeedsActiveTenantsAndBackfillsObjectProtection_AndIsIdempotent(string engine)
    {
        await using var db = await OpenPreStepAsync(engine);
        if (db is null) return;

        await SeedPreStepDataAsync(db);
        await RunStepAsync(db);

        await AssertSeededAndBackfilledStateAsync(db);

        // Idempotent re-run succeeds without duplicating or altering seeded rows.
        await RunStepAsync(db);
        await AssertSeededAndBackfilledStateAsync(db);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Step0036_FreshFullMigration_MatchesShapeWithZeroDeviations(string engine)
    {
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        var latestVersion = steps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var pgDb = await PostgreSqlTestDatabase.CreateAsync();
            if (!pgDb.IsAvailable) return;

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(pgDb.ConnectionString);
            await conn.OpenAsync();
            var verifier = new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
        else
        {
            await using var ssDb = await SqlServerTestDatabase.CreateAsync();
            if (!ssDb.IsAvailable) return;

            await GovernanceSchemaTestSupport.RunSqlServerAsync(ssDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(ssDb.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(ssDb.ConnectionString);
            await conn.OpenAsync();
            var verifier = new GovernanceSchemaShapeVerifier("sqlserver", new SqlServerSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
    }

    private static async Task AssertSeededAndBackfilledStateAsync(Db db)
    {
        var c = db.Connection;
        var domains = Table(db.Engine, "governance_domains");
        var epochs = Table(db.Engine, "governance_epochs");
        var protection = Table(db.Engine, "object_protection");

        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {domains}")).ShouldBe(2);
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {domains} WHERE tenant_id = 't-collided'")).ShouldBe(0);
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {domains} WHERE tenant_id = ''")).ShouldBe(0);

        foreach (var tenant in new[] { "t-alpha", "t-beta" })
        {
            (await ScalarStringAsync(c, $"SELECT name FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe("Unassigned");
            (await ScalarStringAsync(c, $"SELECT default_class FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe("CONFIDENTIAL");
            (await ScalarStringAsync(c, $"SELECT visibility FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe("ON_REQUEST");
            (await ScalarLongAsync(c, $"SELECT CASE WHEN is_system = {(db.Engine == "postgresql" ? "TRUE" : "1")} THEN 1 ELSE 0 END FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe(1);
            (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {domains} WHERE tenant_id = '{tenant}' AND owner_group_sid IS NULL")).ShouldBe(1);
            (await ScalarLongAsync(c, $"SELECT version FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe(1);
            (await ScalarStringAsync(c, $"SELECT created_by FROM {domains} WHERE tenant_id = '{tenant}' AND id = '{UnassignedDomainId}'")).ShouldBe("system:migration-0036");
        }

        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {epochs}")).ShouldBe(2);
        (await ScalarLongAsync(c, $"SELECT epoch FROM {epochs} WHERE tenant_id = 't-alpha'")).ShouldBe(1);
        (await ScalarLongAsync(c, $"SELECT epoch FROM {epochs} WHERE tenant_id = 't-beta'")).ShouldBe(1);
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {epochs} WHERE tenant_id = 't-collided'")).ShouldBe(0);

        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {protection}")).ShouldBe(5);
        (await ScalarStringAsync(c, $"SELECT mask_category FROM {protection} WHERE tenant_id = 't-alpha' AND table_id = 'tbl-1' AND column_name = 'ssn'")).ShouldBe("PII");
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {protection} WHERE tenant_id = 't-alpha' AND table_id = 'tbl-1' AND column_name = 'ssn' AND protection_class IS NULL")).ShouldBe(1);
        (await ScalarStringAsync(c, $"SELECT mask_category FROM {protection} WHERE tenant_id = 't-beta' AND table_id = 'tbl-1' AND column_name = 'ssn'")).ShouldBe("FINANCIAL");
        (await ScalarStringAsync(c, $"SELECT mask_category FROM {protection} WHERE tenant_id = 't-alpha' AND table_id = 'tbl-2' AND column_name = 'legacy_tag'")).ShouldBe("CUSTOM_UNREGISTERED");
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {protection} WHERE tenant_id = 't-beta' AND table_id = 'tbl-2' AND column_name = 'legacy_tag'")).ShouldBe(0);
        (await ScalarStringAsync(c, $"SELECT mask_category FROM {protection} WHERE tenant_id = 't-alpha' AND table_id = 'tbl-2' AND column_name = 'tied_col'")).ShouldBe("TENANT_RANK20");
        (await ScalarStringAsync(c, $"SELECT mask_category FROM {protection} WHERE tenant_id = 't-beta' AND table_id = 'tbl-2' AND column_name = 'tied_col'")).ShouldBe("CONFIDENTIAL");
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {protection} WHERE column_name = 'public_label'")).ShouldBe(0);
        (await ScalarLongAsync(c, $"SELECT COUNT(*) FROM {protection} WHERE tenant_id = 't-collided'")).ShouldBe(0);
    }

    private static async Task SeedPreStepDataAsync(Db db)
    {
        var c = db.Connection;
        var tenants = Table(db.Engine, "tenant_registry");
        var cols = Table(db.Engine, "table_columns");
        var dc = Table(db.Engine, "data_classifications");
        var cc = Table(db.Engine, "column_classifications");
        var nowExpr = db.Engine == "postgresql" ? "CURRENT_TIMESTAMP" : "SYSUTCDATETIME()";

        await ExecAsync(c, $"""
            INSERT INTO {tenants} (normalized_key, tenant_id, collided, first_seen_at) VALUES
                ('T_ALPHA', 't-alpha', 0, '2026-11-01T00:00:00Z'),
                ('T_BETA', 't-beta', 0, '2026-11-01T00:00:00Z'),
                ('T_COLLIDED', 't-collided', 1, '2026-11-01T00:00:00Z'),
                ('T_BLANK', '', 0, '2026-11-01T00:00:00Z');

            INSERT INTO {dc} (scope, code, restriction_rank) VALUES
                ('*', 'PUBLIC', 0),
                ('*', 'CONFIDENTIAL', 20),
                ('*', 'FINANCIAL', 30),
                ('*', 'PII', 40),
                ('*', 'PII_SPECIAL', 60),
                ('t-beta', 'FINANCIAL', 80),
                ('t-alpha', 'TENANT_RANK20', 20);

            INSERT INTO {cols} (id, table_id, column_name, data_type) VALUES
                ('col-multi', 'tbl-1', 'ssn', 'TEXT'),
                ('col-public', 'tbl-1', 'public_label', 'TEXT'),
                ('col-custom', 'tbl-2', 'legacy_tag', 'TEXT'),
                ('col-tie', 'tbl-2', 'tied_col', 'TEXT'),
                ('col-collided', 'tbl-3', 'secret_col', 'TEXT');

            INSERT INTO {cc} (scope, table_column_id, code, source, assigned_by, assigned_at) VALUES
                ('*', 'col-multi', 'PUBLIC', 'ADMIN', 'admin', {nowExpr}),
                ('*', 'col-multi', 'FINANCIAL', 'ADMIN', 'admin', {nowExpr}),
                ('*', 'col-multi', 'PII', 'ADMIN', 'admin', {nowExpr}),
                ('*', 'col-public', 'PUBLIC', 'ADMIN', 'admin', {nowExpr}),
                ('t-alpha', 'col-custom', 'CUSTOM_UNREGISTERED', 'ADMIN', 'admin', {nowExpr}),
                ('*', 'col-tie', 'CONFIDENTIAL', 'ADMIN', 'admin', {nowExpr}),
                ('t-alpha', 'col-tie', 'TENANT_RANK20', 'ADMIN', 'admin', {nowExpr}),
                ('t-collided', 'col-collided', 'PII', 'ADMIN', 'admin', {nowExpr});
            """);
    }

    private static async Task<Db?> OpenPreStepAsync(string engine)
    {
        var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 36 && s.Name == StepName);
        if (engine == "postgresql")
        {
            var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("domains_0036"));
            await conn.OpenAsync();
            await ExecAsync(conn, PostgreSqlPreShape);
            return new Db(engine, conn, step, conn.DisposeAsync);
        }

        var database = await SqlServerTestDatabase.CreateAsync();
        if (!database.IsAvailable)
        {
            return null;
        }

        var sqlConn = new SqlConnection(database.ConnectionString);
        await sqlConn.OpenAsync();
        await ExecAsync(sqlConn, SqlServerPreShape);

        return new Db(engine, sqlConn, step, async () =>
        {
            await sqlConn.DisposeAsync();
            await database.DisposeAsync();
        });
    }

    private static string Table(string engine, string name) => engine == "postgresql" ? name : "dbo." + name.ToUpperInvariant();

    private static async Task RunStepAsync(Db db)
    {
        await using var tx = await db.Connection.BeginTransactionAsync();
        try
        {
            foreach (var batch in db.Step.Batches)
            {
                await ExecAsync(db.Connection, batch, tx);
            }

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    private sealed record Db(string Engine, DbConnection Connection, SqlMigrationStep Step, Func<ValueTask> Dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Dispose();
    }
}
