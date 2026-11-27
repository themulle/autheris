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
/// Migration step 0052 (<c>uc_provisioning</c>, issue #1122) across PostgreSQL and SQL Server: the provisioning ledger and the feedback table are
/// created on top of step 0051, the step is idempotent and the resulting shape matches the declared shape delta.
/// </summary>
public sealed class UcProvisioningMigrationStep0052Tests
{
    private static readonly string[] LedgerColumns =
    [
        "id", "data_source", "securable_type", "full_name", "principal", "principal_external_id", "privilege", "state", "attempts",
        "next_attempt_at", "last_error_code", "applied_at", "verified_at", "created_at", "updated_at"
    ];

    private static readonly string[] FeedbackColumns =
    [
        "tenant_id", "consent_id", "data_source", "state", "reason_code", "updated_at", "verified_at"
    ];

    private static readonly string[] FenceColumns = ["data_source", "fencing_token", "updated_at"];

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Provisioning_tables_are_created_idempotently_with_the_declared_shape(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        allSteps.ShouldContain(s => s.Version == 52 && s.Name == "uc_provisioning");
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            var preOpts = GovernanceSchemaTestSupport.PostgreSqlOptions(db.ConnectionString, GovernanceMigrationMode.Apply);
            using (var preRepo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(preOpts))
            {
                await new PostgreSqlGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 51 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in LedgerColumns)
            {
                (await ColumnExistsPgAsync(conn, "uc_provisioned_privileges", column)).ShouldBeTrue(column);
            }

            foreach (var column in FeedbackColumns)
            {
                (await ColumnExistsPgAsync(conn, "uc_grant_provisioning", column)).ShouldBeTrue(column);
            }

            foreach (var column in FenceColumns)
            {
                (await ColumnExistsPgAsync(conn, "uc_provisioning_fences", column)).ShouldBeTrue(column);
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
                await new SqlServerGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 51 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in LedgerColumns)
            {
                (await ColumnExistsSsAsync(conn, "UC_PROVISIONED_PRIVILEGES", column)).ShouldBeTrue(column);
            }

            foreach (var column in FeedbackColumns)
            {
                (await ColumnExistsSsAsync(conn, "UC_GRANT_PROVISIONING", column)).ShouldBeTrue(column);
            }

            foreach (var column in FenceColumns)
            {
                (await ColumnExistsSsAsync(conn, "UC_PROVISIONING_FENCES", column)).ShouldBeTrue(column);
            }

            var verifier = new GovernanceSchemaShapeVerifier("sqlserver", new SqlServerSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
    }

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
