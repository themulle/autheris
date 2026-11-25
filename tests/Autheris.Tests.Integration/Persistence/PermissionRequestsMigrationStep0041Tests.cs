using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
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
/// Migration step 0041 (<c>permission_requests_and_grant_kinds</c>) across PostgreSQL and SQL Server:
/// schema delta, D13 start-fresh purge with archive/purge report, untouched audit log and profile definitions,
/// removed-scope workflow route cleanup, epoch bump, idempotent re-run, and shape verification.
/// </summary>
[Trait("Category", "Security")]
public sealed class PermissionRequestsMigrationStep0041Tests
{
    private static readonly string[] PurgedTables =
    [
        "CONSENT_COLUMN_RULES",
        "CONSENT_ROW_FILTERS",
        "CONSENT_ROW_SCOPES",
        "CONSENT_WRITE_RULES",
        // consent_classification_access is dropped by step 0044 (W5-03); its removal is asserted in RetireClassificationClearMigrationStep0044Tests.
        "consent_approving_owners",
        "CONSENTS",
        "GRANT_RECORDS",
        "GRANT_RECORD_HEADS",
        "APPROVAL_STEPS",
        "PROFILE_ASSIGNMENT_ITEMS",
        "access_profile_assignments",
        "CONSENT_EVENT_DELIVERIES",
        "CONSENT_EVENTS",
        "CONSENT_REQUESTS",
        "DATA_OWNER_DELEGATIONS",
        "TABLE_OWNERS",
        "DATA_OWNERS"
    ];

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task No_residual_rows_in_any_listed_table_and_audit_and_profiles_untouched(string engine)
    {
        var allSteps = SqlMigrationStep.LoadEmbedded(engine);
        var latestVersion = allSteps.Max(s => s.Version);
        var expectedShape = GovernanceSchemaShapeLoader.LoadAt(engine, latestVersion, allSteps.ToDictionary(s => s.Version, s => s.ShapeDelta));

        if (engine == "postgresql")
        {
            await using var pgDb = await PostgreSqlTestDatabase.CreateAsync();
            if (!pgDb.IsAvailable) return;

            // Run up to step 39 first, seed legacy grants/requests/routes/audit/profiles, then apply step 40.
            var pgPreOpts = GovernanceSchemaTestSupport.PostgreSqlOptions(pgDb.ConnectionString, GovernanceMigrationMode.Apply);
            using (var pgPreRepo = GovernanceSchemaTestSupport.UnmigratedPostgreSql(pgPreOpts))
            {
                await new PostgreSqlGovernanceMigrationRunner(pgPreRepo, pgPreOpts) { StepVersionCapOverride = 40 }.RunAsync();
            }
            await SeedPrePurgeRowsPgAsync(pgDb.ConnectionString);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new NpgsqlConnection(pgDb.ConnectionString);
            await conn.OpenAsync();
            await AssertPostPurgePgAsync(conn);
            var verifier = new GovernanceSchemaShapeVerifier("postgresql", new PostgreSqlSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
        else
        {
            await using var ssDb = await SqlServerTestDatabase.CreateAsync();
            if (!ssDb.IsAvailable) return;

            var ssPreOpts = GovernanceSchemaTestSupport.SqlServerOptions(ssDb.ConnectionString, GovernanceMigrationMode.Apply);
            using (var ssPreRepo = GovernanceSchemaTestSupport.UnmigratedSqlServer(ssPreOpts))
            {
                await new SqlServerGovernanceMigrationRunner(ssPreRepo, ssPreOpts) { StepVersionCapOverride = 40 }.RunAsync();
            }
            await SeedPrePurgeRowsSsAsync(ssDb.ConnectionString);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(ssDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(ssDb.ConnectionString, GovernanceMigrationMode.Apply);
            await GovernanceSchemaTestSupport.RunSqlServerAsync(ssDb.ConnectionString, GovernanceMigrationMode.Verify);

            await using var conn = new SqlConnection(ssDb.ConnectionString);
            await conn.OpenAsync();
            await AssertPostPurgeSsAsync(conn);
            var verifier = new GovernanceSchemaShapeVerifier("sqlserver", new SqlServerSchemaCatalogReader());
            var result = await verifier.VerifyAsync(conn, expectedShape, CancellationToken.None);
            result.Deviations.ShouldBeEmpty();
            result.ExtraTables.ShouldBeEmpty();
        }
    }

    private static async Task SeedPrePurgeRowsPgAsync(string connStr)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES ('do-1', 'S-1-5-O1', 'CORP\\o1', 'O1', 'o1@x.test', 1);
            INSERT INTO GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES ('tenant-a', 4, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
            INSERT INTO access_profiles (profile_id, name, tenant_id, created_by, created_at) VALUES ('prof-1', 'FinanceAnalyst', 'tenant-a', 'admin', '2026-01-01T00:00:00Z');
            INSERT INTO access_profile_assignments (tenant_id, profile_id, subject_type, subject, subject_norm, created_by, assigned_at, kind, renewal_count) VALUES ('tenant-a', 'prof-1', 'User', 'S-1-5-U1', 's-1-5-u1', 'admin', '2026-01-01T00:00:00Z', 'Standard', 0);
            INSERT INTO CONSENT_WORKFLOW_ROUTES (tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days, request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at)
            VALUES ('tenant-a', 'DATA_OWNER', 'do-1', 'INTERNAL', NULL, 30, 14, 24, TRUE, 'admin', NOW()),
                   ('tenant-a', 'TENANT', '*', 'INTERNAL', NULL, 90, 14, 24, TRUE, 'admin', NOW());
            INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, decision, trace_id, details_json, tenant_id, seq, prev_hash, entry_hash)
            VALUES ('audit-pre-1', '2026-01-01T00:00:00Z', 'SERVICE_STARTED', 'SYSTEM', '*', 'ALLOW', 't1', '{}', 'platform', 1, '0000000000000000000000000000000000000000000000000000000000000000', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedPrePurgeRowsSsAsync(string connStr)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(@"
            INSERT INTO dbo.DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES (N'do-1', N'S-1-5-O1', N'CORP\o1', N'O1', N'o1@x.test', 1);
            INSERT INTO dbo.GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES (N'tenant-a', 4, N'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
            INSERT INTO dbo.ACCESS_PROFILES (profile_id, name, tenant_id, created_by, created_at) VALUES (N'prof-1', N'FinanceAnalyst', N'tenant-a', N'admin', N'2026-01-01T00:00:00Z');
            INSERT INTO dbo.ACCESS_PROFILE_ASSIGNMENTS (tenant_id, profile_id, subject_type, subject, subject_norm, created_by, assigned_at, kind, renewal_count) VALUES (N'tenant-a', N'prof-1', N'User', N'S-1-5-U1', N's-1-5-u1', N'admin', N'2026-01-01T00:00:00Z', N'Standard', 0);
            INSERT INTO dbo.CONSENT_WORKFLOW_ROUTES (tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days, request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at)
            VALUES (N'tenant-a', N'DATA_OWNER', N'do-1', N'INTERNAL', NULL, 30, 14, 24, 1, N'admin', SYSUTCDATETIME()),
                   (N'tenant-a', N'TENANT', N'*', N'INTERNAL', NULL, 90, 14, 24, 1, N'admin', SYSUTCDATETIME());
            INSERT INTO dbo.AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, decision, trace_id, details_json, tenant_id, seq, prev_hash, entry_hash)
            VALUES (N'audit-pre-1', N'2026-01-01T00:00:00Z', N'SERVICE_STARTED', N'SYSTEM', N'*', N'ALLOW', N't1', N'{}', N'platform', 1, N'0000000000000000000000000000000000000000000000000000000000000000', N'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
        ", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AssertPostPurgePgAsync(NpgsqlConnection conn)
    {
        foreach (var tbl in PurgedTables)
        {
            await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {tbl}", conn);
            Convert.ToInt64(await countCmd.ExecuteScalarAsync()).ShouldBe(0, $"Purged table {tbl} should be empty.");
        }

        await using var auditCmd = new NpgsqlCommand("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES", conn);
        Convert.ToInt64(await auditCmd.ExecuteScalarAsync()).ShouldBeGreaterThanOrEqualTo(1);

        await using var profCmd = new NpgsqlCommand("SELECT COUNT(*) FROM access_profiles WHERE profile_id = 'prof-1'", conn);
        Convert.ToInt64(await profCmd.ExecuteScalarAsync()).ShouldBe(1);

        await using var archiveCmd = new NpgsqlCommand("SELECT COUNT(*) FROM grant_record_heads_archive WHERE tenant_id = 'tenant-a' AND last_seq = 4", conn);
        Convert.ToInt64(await archiveCmd.ExecuteScalarAsync()).ShouldBe(1);

        await using var reportCmd = new NpgsqlCommand("SELECT COUNT(*) FROM migration_purge_report WHERE step = '0041_permission_requests_and_grant_kinds'", conn);
        Convert.ToInt64(await reportCmd.ExecuteScalarAsync()).ShouldBe(18);

        await using var routeCmd = new NpgsqlCommand("SELECT COUNT(*) FROM CONSENT_WORKFLOW_ROUTES WHERE scope_kind IN ('DATA_OWNER', 'DATA_SOURCE')", conn);
        Convert.ToInt64(await routeCmd.ExecuteScalarAsync()).ShouldBe(0);

        await using var tenantRouteCmd = new NpgsqlCommand("SELECT COUNT(*) FROM CONSENT_WORKFLOW_ROUTES WHERE scope_kind = 'TENANT'", conn);
        Convert.ToInt64(await tenantRouteCmd.ExecuteScalarAsync()).ShouldBe(1);
    }

    private static async Task AssertPostPurgeSsAsync(SqlConnection conn)
    {
        foreach (var tbl in PurgedTables)
        {
            await using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.{tbl.ToUpperInvariant()}", conn);
            Convert.ToInt64(await countCmd.ExecuteScalarAsync()).ShouldBe(0, $"Purged table {tbl} should be empty.");
        }

        await using var auditCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.AUDIT_LOG_ENTRIES", conn);
        Convert.ToInt64(await auditCmd.ExecuteScalarAsync()).ShouldBeGreaterThanOrEqualTo(1);

        await using var profCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.ACCESS_PROFILES WHERE profile_id = N'prof-1'", conn);
        Convert.ToInt64(await profCmd.ExecuteScalarAsync()).ShouldBe(1);

        await using var archiveCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.GRANT_RECORD_HEADS_ARCHIVE WHERE tenant_id = N'tenant-a' AND last_seq = 4", conn);
        Convert.ToInt64(await archiveCmd.ExecuteScalarAsync()).ShouldBe(1);

        await using var reportCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.MIGRATION_PURGE_REPORT WHERE step = N'0041_permission_requests_and_grant_kinds'", conn);
        Convert.ToInt64(await reportCmd.ExecuteScalarAsync()).ShouldBe(18);

        await using var routeCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.CONSENT_WORKFLOW_ROUTES WHERE scope_kind IN (N'DATA_OWNER', N'DATA_SOURCE')", conn);
        Convert.ToInt64(await routeCmd.ExecuteScalarAsync()).ShouldBe(0);

        await using var tenantRouteCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.CONSENT_WORKFLOW_ROUTES WHERE scope_kind = N'TENANT'", conn);
        Convert.ToInt64(await tenantRouteCmd.ExecuteScalarAsync()).ShouldBe(1);
    }
}
