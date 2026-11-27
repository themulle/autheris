using System;
using System.Data.Common;
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
/// Migration step 0051 (<c>consents_request_generation</c>, issue #1187) across PostgreSQL and SQL Server: the request-only unique index is
/// replaced by two filtered unique indexes keyed on item and renewal generation. Distinct items and successive renewals of one request are
/// accepted, a duplicate (request, item, generation) is rejected, and rows without a request or with the sync marker stay unconstrained.
/// </summary>
public sealed class ConsentsRequestGenerationMigrationStep0051Tests
{
    private const string SyncMarker = "0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19";

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Request_index_is_keyed_on_item_and_renewal_generation_and_the_step_is_idempotent(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        allSteps.ShouldContain(s => s.Version == 51 && s.Name == "consents_request_generation");
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            await AssertIndexesAsync(conn, "SELECT COUNT(*) FROM pg_indexes WHERE tablename = 'consents' AND indexname = @n", "@n");
            await AssertRulesAsync(conn, "CONSENTS", NpgsqlUniqueViolation);

            var verifier = new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
        else
        {
            await using var db = await SqlServerTestDatabase.CreateAsync();
            if (!db.IsAvailable) return;

            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(db.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(db.ConnectionString);
            await conn.OpenAsync();
            await AssertIndexesAsync(conn, "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CONSENTS') AND name = @n", "@n");
            await AssertRulesAsync(conn, "dbo.CONSENTS", SqlServerUniqueViolation);

            var verifier = new GovernanceSchemaShapeVerifier("sqlserver", new SqlServerSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
    }

    private static bool NpgsqlUniqueViolation(Exception e) => e is PostgresException { SqlState: "23505" };

    private static bool SqlServerUniqueViolation(Exception e) => e is SqlException { Number: 2601 or 2627 };

    private static async Task AssertIndexesAsync(DbConnection conn, string countSql, string parameter)
    {
        (await CountAsync(conn, countSql, parameter, "ux_consents_request")).ShouldBe(0, "the request-only index is dropped");
        (await CountAsync(conn, countSql, parameter, "ux_consents_request_generation")).ShouldBe(1);
        (await CountAsync(conn, countSql, parameter, "ux_consents_request_item_generation")).ShouldBe(1);
    }

    private static async Task<long> CountAsync(DbConnection conn, string sql, string parameter, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = parameter;
        p.Value = name;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task AssertRulesAsync(DbConnection conn, string table, Func<Exception, bool> isUniqueViolation)
    {
        var request = Guid.NewGuid().ToString();

        // Request level (item NULL): the same generation twice is rejected, the next generation is accepted.
        (await TryInsertAsync(conn, table, isUniqueViolation, request, null, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, request, null, 0)).ShouldBeFalse("duplicate (request, generation) is rejected");
        (await TryInsertAsync(conn, table, isUniqueViolation, request, null, 1)).ShouldBeTrue("a successive renewal generation is accepted");

        // Item level: distinct items and generations are accepted, a duplicate (request, item, generation) is rejected.
        var items = Guid.NewGuid().ToString();
        (await TryInsertAsync(conn, table, isUniqueViolation, items, 0, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, items, 1, 0)).ShouldBeTrue("a distinct item of the same request is accepted");
        (await TryInsertAsync(conn, table, isUniqueViolation, items, 0, 1)).ShouldBeTrue("a renewal generation of the same item is accepted");
        (await TryInsertAsync(conn, table, isUniqueViolation, items, 1, 0)).ShouldBeFalse("duplicate (request, item, generation) is rejected");

        // Rows without a request id and rows carrying the sync marker stay unconstrained.
        (await TryInsertAsync(conn, table, isUniqueViolation, null, null, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, null, null, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, SyncMarker, null, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, SyncMarker, null, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, SyncMarker, 0, 0)).ShouldBeTrue();
        (await TryInsertAsync(conn, table, isUniqueViolation, SyncMarker, 0, 0)).ShouldBeTrue();
    }

    private static async Task<bool> TryInsertAsync(
        DbConnection conn, string table, Func<Exception, bool> isUniqueViolation, string? requestId, int? itemNo, int renewalCount)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {table} (id, table_id, consent_request_id, effect, grantee_type, valid_from, valid_to, is_revoked, tenant_id, profile_item_no, renewal_count) " +
            "VALUES (@id, @tid, @req, 'Allow', 'User', '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z', 0, 'tenant-a', @item, @gen)";
        Add(cmd, "@id", Guid.NewGuid().ToString());
        Add(cmd, "@tid", Guid.NewGuid().ToString());
        Add(cmd, "@req", requestId);
        Add(cmd, "@item", itemNo);
        Add(cmd, "@gen", renewalCount);
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch (Exception e) when (isUniqueViolation(e))
        {
            return false;
        }
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
