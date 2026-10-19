namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

/// <summary>
/// Security conditions SEC-SEC210-04 and SEC-SEC210-05 (REQ-SEC-210):
/// Forward governance database upgrade from v1.1.x to v1.2.0 is non-destructive,
/// preserves historical consents and audit logs, backfills normalized identifiers,
/// and achieves 0 schema drift against GovernanceSchemaShapeVerifier.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceMigrationSecurityTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Autheris.sln repo root not found.");
    }

    private static string UpgradeScriptPath() =>
        Path.Combine(FindRepoRoot(), "tools", "migrations", "upgrade_governance_v1_1_to_v1_2.sql");

    private static string V118FixturePath() =>
        Path.Combine(FindRepoRoot(), "tests", "Autheris.Tests.Unit", "Governance", "Fixtures", "governance-v1.1.8-preshim.sqlite.sql");

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_SEC210_04_UpgradeScript_NonDestructivelyUpgradesV11Database_WithZeroShapeDrift()
    {
        // SEC-SEC210-04 & SEC-SEC210-05: Non-destructively upgrade v1.1.x database to v1.2.0 with 0 shape drift
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // 1. Initialize v1.1.x database schema
        string v118Ddl = await File.ReadAllTextAsync(V118FixturePath());
        await using (var initCmd = connection.CreateCommand())
        {
            initCmd.CommandText = v118Ddl;
            await initCmd.ExecuteNonQueryAsync();
        }

        // 2. Seed pre-existing production data
        const string consentId = "consent-prod-001";
        const string tableId = "table-orders-001";
        const string rawGranteeSid = " corp\\alice.smith ";
        const string auditEntryId = "audit-entry-001";
        const string profileId = "profile-finance";
        const string rawSubject = " bob@corp.local ";

        await using (var seedCmd = connection.CreateCommand())
        {
            seedCmd.CommandText = @"
                INSERT INTO TABLES (id, source_type, source_name, schema_name, table_name, display_name, sensitivity, requires_four_eyes, is_active)
                VALUES (@tableId, 'sqlserver', 'MainDb', 'dbo', 'Orders', 'Customer Orders', 'Confidential', 0, 1);

                INSERT INTO CONSENTS (id, table_id, effect, grantee_type, grantee_sid, valid_from, valid_to, is_revoked, tenant_id)
                VALUES (@consentId, @tableId, 'ALLOW', 'User', @granteeSid, '2026-01-01', '2027-01-01', 0, 'tenant-corp');

                INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id)
                VALUES (@auditId, '2026-10-15T12:00:00Z', 'QueryExecution', 'S-1-5-21', 'dbo.Orders', 'ALLOW', 'trace-01', '{}', 'GENESIS', 'hash-001', 'tenant-corp');

                INSERT INTO ACCESS_PROFILES (profile_id, tenant_id, name, created_by, created_at)
                VALUES (@profileId, 'tenant-corp', 'Finance Team', 'admin', '2026-01-01');

                INSERT INTO ACCESS_PROFILE_ASSIGNMENTS (profile_id, tenant_id, subject, subject_type, assigned_at)
                VALUES (@profileId, 'tenant-corp', @subject, 'User', '2026-01-01');
            ";
            seedCmd.Parameters.AddWithValue("@tableId", tableId);
            seedCmd.Parameters.AddWithValue("@consentId", consentId);
            seedCmd.Parameters.AddWithValue("@granteeSid", rawGranteeSid);
            seedCmd.Parameters.AddWithValue("@auditId", auditEntryId);
            seedCmd.Parameters.AddWithValue("@profileId", profileId);
            seedCmd.Parameters.AddWithValue("@subject", rawSubject);
            await seedCmd.ExecuteNonQueryAsync();
        }

        // 3. Execute upgrade script
        string upgradeSql = await File.ReadAllTextAsync(UpgradeScriptPath());
        await using (var upgradeCmd = connection.CreateCommand())
        {
            upgradeCmd.CommandText = upgradeSql;
            await upgradeCmd.ExecuteNonQueryAsync();
        }

        // 4. Verify shape with GovernanceSchemaShapeVerifier -> MUST BE 0 DEVIATIONS
        var verifier = new GovernanceSchemaShapeVerifier("sqlite", new SqliteSchemaCatalogReader());
        var verificationResult = await verifier.VerifyAsync(connection, CancellationToken.None);

        verificationResult.Deviations.ShouldBeEmpty(
            "Expected 0 schema drift after upgrade script execution: " +
            string.Join(Environment.NewLine, verificationResult.Deviations));

        // 5. Verify data integrity and backfill (SEC-SEC210-05)
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT grantee_sid, grantee_sid_norm FROM CONSENTS WHERE id = @id";
            queryCmd.Parameters.AddWithValue("@id", consentId);
            await using var reader = await queryCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe(rawGranteeSid);
            reader.GetString(1).ShouldBe("CORP\\ALICE.SMITH");
        }

        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT subject, subject_norm FROM ACCESS_PROFILE_ASSIGNMENTS WHERE profile_id = @id";
            queryCmd.Parameters.AddWithValue("@id", profileId);
            await using var reader = await queryCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe(rawSubject);
            reader.GetString(1).ShouldBe("BOB@CORP.LOCAL");
        }

        // 6. Verify audit logs remain intact
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE id = @id";
            queryCmd.Parameters.AddWithValue("@id", auditEntryId);
            var count = Convert.ToInt64(await queryCmd.ExecuteScalarAsync());
            count.ShouldBe(1);
        }

        // 7. Verify autheris_schema_migrations version 1 recorded
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = "SELECT name, checksum FROM autheris_schema_migrations WHERE version = 1";
            await using var reader = await queryCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetString(0).ShouldBe("baseline_governance_schema");
            reader.GetString(1).ShouldBe("5B2722FAAB8D29BE6B490F9A1B18BA3BEF882260EBF6225706E55349647575AA");
        }
    }
}
