using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// Migration step 0050 (<c>external_system_role</c>, issue #1161) across PostgreSQL and SQL Server: the role column is added to the external
/// workflow systems on top of step 0049, existing systems become WORKFLOW, the step is idempotent and the resulting shape matches the declared
/// shape delta.
/// </summary>
public sealed class ExternalSystemRoleMigrationStep0050Tests
{
    private static readonly string[] Columns = ["id", "tenant_id", "client_id", "enabled", "system_role"];

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Role_column_is_added_idempotently_and_existing_systems_are_workflow_systems(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        allSteps.ShouldContain(s => s.Version == 50 && s.Name == "external_system_role");
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            var preOpts = GovernanceSchemaTestSupport.PostgreSqlOptions(db.ConnectionString, GovernanceMigrationMode.Apply);
            using (var preRepo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(preOpts))
            {
                await new PostgreSqlGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 49 }.RunAsync();
            }

            await using (var seed = new NpgsqlConnection(db.ConnectionString))
            {
                await seed.OpenAsync();
                await using var insert = new NpgsqlCommand(InsertExistingSystemSql.Replace(", 1, 'admin'", ", TRUE, 'admin'", StringComparison.Ordinal), seed);
                await insert.ExecuteNonQueryAsync();
            }

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in Columns)
            {
                (await ColumnExistsPgAsync(conn, "external_workflow_systems", column)).ShouldBeTrue(column);
            }

            await using (var roleCmd = new NpgsqlCommand("SELECT system_role FROM external_workflow_systems WHERE id = 'existing-system'", conn))
            {
                ((string?)await roleCmd.ExecuteScalarAsync()).ShouldBe("WORKFLOW");
            }

            var verifier = new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
        else
        {
            await using var db = await SqlServerTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            var preOpts = GovernanceSchemaTestSupport.SqlServerOptions(db.ConnectionString, GovernanceMigrationMode.Apply);
            using (var preRepo = GovernanceSchemaTestSupport.UnmigratedSqlServer(preOpts))
            {
                await new SqlServerGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 49 }.RunAsync();
            }

            await using (var seed = new SqlConnection(db.ConnectionString))
            {
                await seed.OpenAsync();
                await using var insert = new SqlCommand(InsertExistingSystemSql.Replace("EXTERNAL_WORKFLOW_SYSTEMS", "dbo.EXTERNAL_WORKFLOW_SYSTEMS", StringComparison.Ordinal), seed);
                await insert.ExecuteNonQueryAsync();
            }

            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in Columns)
            {
                (await ColumnExistsSsAsync(conn, "EXTERNAL_WORKFLOW_SYSTEMS", column)).ShouldBeTrue(column);
            }

            await using (var roleCmd = new SqlCommand("SELECT system_role FROM dbo.EXTERNAL_WORKFLOW_SYSTEMS WHERE id = N'existing-system'", conn))
            {
                ((string?)await roleCmd.ExecuteScalarAsync()).ShouldBe("WORKFLOW");
            }

            var verifier = new GovernanceSchemaShapeVerifier("sqlserver", new SqlServerSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
    }

    private const string InsertExistingSystemSql =
        "INSERT INTO EXTERNAL_WORKFLOW_SYSTEMS (id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at) " +
        "VALUES ('existing-system', 'tenant-a', 'existing', 'client-existing', 'https://existing.example.com/hook', 'protected', 1, 'admin', '2026-11-26T12:00:00Z')";

    private static async Task<bool> ColumnExistsPgAsync(NpgsqlConnection conn, string table, string column)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = @t AND column_name = @c", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> ColumnExistsSsAsync(SqlConnection conn, string table, string column)
    {
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c", conn);
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }
}
