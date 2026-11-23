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
/// Migration step 0042 (<c>expiry_notice_stages</c>) across PostgreSQL and SQL Server: the stage column replaces the
/// single-shot notice timestamp, the workflow route notice lead time is dropped, the step is idempotent and the
/// resulting shape matches the declared shape delta.
/// </summary>
[Trait("Category", "Security")]
public sealed class ExpiryNoticeStagesMigrationStep0042Tests
{
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Stage_column_replaces_notice_timestamp_and_route_lead_hours(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            var preOpts = GovernanceSchemaTestSupport.PostgreSqlOptions(db.ConnectionString, GovernanceMigrationMode.Apply);
            using (var preRepo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(preOpts))
            {
                await new PostgreSqlGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 41 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            (await ColumnExistsPgAsync(conn, "consents", "expiry_notice_stage")).ShouldBeTrue();
            (await ColumnExistsPgAsync(conn, "consents", "expiry_notice_sent_at")).ShouldBeFalse();
            (await ColumnExistsPgAsync(conn, "consent_workflow_routes", "expiry_notice_lead_hours")).ShouldBeFalse();

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
                await new SqlServerGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 41 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            (await ColumnExistsSsAsync(conn, "CONSENTS", "expiry_notice_stage")).ShouldBeTrue();
            (await ColumnExistsSsAsync(conn, "CONSENTS", "expiry_notice_sent_at")).ShouldBeFalse();
            (await ColumnExistsSsAsync(conn, "CONSENT_WORKFLOW_ROUTES", "expiry_notice_lead_hours")).ShouldBeFalse();

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
