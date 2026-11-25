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
/// Migration step 0047 (<c>approval_step_identifiers</c>, issue #1121) across PostgreSQL and SQL Server: the approver identifier column is added
/// to the approval steps on top of step 0046, the step is idempotent and the resulting shape matches the declared shape delta.
/// </summary>
public sealed class ApprovalStepIdentifiersMigrationStep0047Tests
{
    private static readonly string[] Columns = ["id", "consent_request_id", "step_number", "approver_sid", "decision", "approver_identifiers_json"];

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Approver_identifier_column_is_added_idempotently_with_the_declared_shape(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        allSteps.ShouldContain(s => s.Version == 47 && s.Name == "approval_step_identifiers");
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            var preOpts = GovernanceSchemaTestSupport.PostgreSqlOptions(db.ConnectionString, GovernanceMigrationMode.Apply);
            using (var preRepo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(preOpts))
            {
                await new PostgreSqlGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 46 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in Columns)
            {
                (await ColumnExistsPgAsync(conn, "approval_steps", column)).ShouldBeTrue(column);
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
                await new SqlServerGovernanceMigrationRunner(preRepo, preOpts) { StepVersionCapOverride = 46 }.RunAsync();
            }

            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            foreach (var column in Columns)
            {
                (await ColumnExistsSsAsync(conn, "APPROVAL_STEPS", column)).ShouldBeTrue(column);
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
