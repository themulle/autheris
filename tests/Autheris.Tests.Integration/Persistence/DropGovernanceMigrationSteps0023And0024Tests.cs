using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

[Trait("Category", "Security")]
public sealed class DropGovernanceMigrationSteps0023And0024Tests
{
    private const string Pre0023Shape = """
        CREATE SCHEMA pre_step;
        SET search_path TO pre_step;
        CREATE TABLE table_columns (id TEXT PRIMARY KEY, table_id TEXT NOT NULL, column_name TEXT NOT NULL, is_sensitive INTEGER NOT NULL);
        CREATE TABLE column_masking_rules (id TEXT PRIMARY KEY, table_column_id TEXT NOT NULL, rule_type TEXT NOT NULL, pattern_or_format TEXT NULL, replacement TEXT NULL, hmac_key_id TEXT NULL);
        CREATE TABLE data_classifications (scope VARCHAR(64) NOT NULL, code VARCHAR(64) NOT NULL, restriction_rank SMALLINT NOT NULL, PRIMARY KEY (scope, code));
        CREATE TABLE column_classifications (
            scope VARCHAR(64) NOT NULL, table_column_id TEXT NOT NULL, code VARCHAR(64) NOT NULL, source VARCHAR(16) NOT NULL,
            assigned_by VARCHAR(256) NOT NULL, assigned_at TIMESTAMPTZ NOT NULL, PRIMARY KEY (scope, table_column_id, code));
        CREATE TABLE column_mask_overrides (
            id UUID PRIMARY KEY, scope VARCHAR(64) NOT NULL, table_column_id TEXT NOT NULL, code VARCHAR(64) NULL, mask_function VARCHAR(32) NOT NULL,
            params_json TEXT NULL, key_ref VARCHAR(128) NULL, catalog_text VARCHAR(8) NOT NULL DEFAULT 'VISIBLE', updated_by VARCHAR(256) NOT NULL, updated_at TIMESTAMPTZ NOT NULL);
        CREATE UNIQUE INDEX ux_column_mask_overrides_all ON column_mask_overrides (scope, table_column_id) WHERE code IS NULL;
        CREATE TABLE consents (id TEXT PRIMARY KEY, tenant_id VARCHAR(128) NOT NULL, is_revoked INTEGER NOT NULL);
        CREATE TABLE consent_column_rules (id TEXT PRIMARY KEY, consent_id TEXT NOT NULL, table_column_id TEXT NOT NULL, access_level INTEGER NOT NULL);
        CREATE TABLE consent_classification_access (
            tenant_id VARCHAR(128) NOT NULL, consent_id TEXT NOT NULL, code VARCHAR(64) NOT NULL, access_level SMALLINT NOT NULL,
            valid_until TIMESTAMPTZ NULL, approved_by_sid VARCHAR(256) NULL, approved_at TIMESTAMPTZ NOT NULL, PRIMARY KEY (tenant_id, consent_id, code));
        INSERT INTO data_classifications VALUES ('*', 'PUBLIC', 0), ('*', 'PII', 40);
        INSERT INTO table_columns VALUES ('c-deny', 't', 'deny_col', 1), ('c-public', 't', 'public_col', 1), ('c-plain', 't', 'plain_col', 1), ('c-clean', 't', 'clean_col', 0);
        INSERT INTO column_masking_rules VALUES ('r1', 'c-deny', 'DENY', NULL, NULL, NULL);
        -- (a) the override the step 0020 backfill created for the DENY rule: a visible redaction
        INSERT INTO column_mask_overrides VALUES ('00000000-0000-0000-0000-000000000001', '*', 'c-deny', NULL, 'TYPE_SAFE_REDACT', '{"constant":"***"}', NULL, 'VISIBLE', 'system:migration-0020', CURRENT_TIMESTAMP);
        INSERT INTO column_classifications VALUES ('*', 'c-deny', 'PII', 'MIGRATION', 'system:migration-0020', CURRENT_TIMESTAMP);
        -- consents: Clear rule on a legacy-sensitive column (no PII row yet / MASK row from the 0021 backfill), and a Clear rule on a clean column
        INSERT INTO consents VALUES ('k-missing', 't1', 0), ('k-backfilled', 't1', 0), ('k-clean', 't1', 0), ('k-revoked', 't1', 1), ('k-admin', 't1', 0), ('k-classified', 't1', 0);
        INSERT INTO consent_column_rules VALUES ('r1', 'k-missing', 'c-plain', 2), ('r2', 'k-backfilled', 'c-public', 2), ('r3', 'k-clean', 'c-clean', 2), ('r4', 'k-revoked', 'c-plain', 2), ('r5', 'k-admin', 'c-plain', 2), ('r6', 'k-classified', 'c-plain', 2), ('r7', 'k-classified', 'c-deny', 2);
        INSERT INTO consent_classification_access VALUES ('t1', 'k-backfilled', 'PII', 1, NULL, NULL, CURRENT_TIMESTAMP), ('t1', 'k-admin', 'PII', 1, NULL, 'approver', CURRENT_TIMESTAMP), ('t1', 'k-classified', 'PII', 1, NULL, NULL, CURRENT_TIMESTAMP);
        -- (b) flagged sensitive, but classified PUBLIC after step 0020
        INSERT INTO column_classifications VALUES ('*', 'c-public', 'PUBLIC', 'ADMIN', 'admin', CURRENT_TIMESTAMP);
        """;

    [Fact]
    public async Task Migration_0023_KeepsDenyDenied_ProtectsPublicClassified_AndDropsLegacyObjects()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql").Single(s => s.Version == 23 && s.Name == "drop_is_sensitive");
        await using var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("drop_is_sensitive_step"));
        await conn.OpenAsync();
        await Exec(conn, Pre0023Shape);

        foreach (var batch in step.Batches)
        {
            await Exec(conn, batch);
        }

        // (a) the override carries deny and the read mapper yields DENY
        await using (var cmd = new NpgsqlCommand("SELECT mask_function, params_json, key_ref FROM column_mask_overrides WHERE table_column_id = 'c-deny' AND scope = '*' AND code IS NULL", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).ShouldBeTrue();
            var (function, parameters) = (reader.GetString(0), reader.GetString(1));
            parameters.ShouldContain("\"deny\":true");
            MaskingRuleOverrideMapper.ToRule(function, parameters, null, Guid.NewGuid()).RuleType.ShouldBe("DENY");
            (await reader.ReadAsync()).ShouldBeFalse();
        }

        // (b) PII is added next to PUBLIC; (c) a plain sensitive column gets PII; a clean column stays unclassified
        (await Scalar(conn, "SELECT COUNT(*) FROM column_classifications WHERE table_column_id = 'c-public' AND code = 'PII'")).ShouldBe(1);
        (await Scalar(conn, "SELECT COUNT(*) FROM column_classifications WHERE table_column_id = 'c-plain' AND code = 'PII'")).ShouldBe(1);
        (await Scalar(conn, "SELECT COUNT(*) FROM column_classifications WHERE table_column_id = 'c-clean'")).ShouldBe(0);
        (await Scalar(conn, "SELECT COUNT(*) FROM column_classifications WHERE table_column_id = 'c-deny' AND code = 'PII'")).ShouldBe(1);

        // consent clearance parity: CLEAR rows for active consents with a Clear rule on a newly classified column; nothing for clean, revoked or admin-decided rows
        (await Scalar(conn, "SELECT access_level FROM consent_classification_access WHERE consent_id = 'k-missing' AND code = 'PII'")).ShouldBe(2);
        (await Scalar(conn, "SELECT access_level FROM consent_classification_access WHERE consent_id = 'k-backfilled' AND code = 'PII'")).ShouldBe(2);
        (await Scalar(conn, "SELECT COUNT(*) FROM consent_classification_access WHERE consent_id IN ('k-clean', 'k-revoked')")).ShouldBe(0);
        (await Scalar(conn, "SELECT access_level FROM consent_classification_access WHERE consent_id = 'k-admin' AND code = 'PII'")).ShouldBe(1);
        // a consent that already holds a Clear rule on a column with a non-PUBLIC global classification keeps its MASK row
        (await Scalar(conn, "SELECT access_level FROM consent_classification_access WHERE consent_id = 'k-classified' AND code = 'PII'")).ShouldBe(1);

        // (c) a legacy rule-less sensitive column has no override; the legacy objects are gone
        (await Scalar(conn, "SELECT COUNT(*) FROM column_mask_overrides WHERE table_column_id = 'c-plain'")).ShouldBe(0);
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'pre_step' AND table_name = 'column_masking_rules'")).ShouldBe(0);
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'table_columns' AND column_name = 'is_sensitive'")).ShouldBe(0);
    }

    [Fact]
    public async Task Migration_0024_AbortsOnAnUnrevokedRoleConsent_AndDropsNothing()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql").Single(s => s.Version == 24 && s.Name == "drop_superseded_governance_columns");
        await using var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("drop_superseded_step"));
        await conn.OpenAsync();
        await Exec(conn, """
            CREATE SCHEMA pre_step;
            SET search_path TO pre_step;
            CREATE TABLE consents (id TEXT PRIMARY KEY, role_id TEXT NULL, role_name TEXT NULL, is_revoked INTEGER NOT NULL);
            CREATE TABLE roles (id TEXT PRIMARY KEY);
            CREATE TABLE role_members (id TEXT PRIMARY KEY);
            CREATE TABLE access_policies (id TEXT PRIMARY KEY);
            CREATE TABLE access_policy_grantees (id TEXT PRIMARY KEY);
            CREATE TABLE access_policy_selectors (id TEXT PRIMARY KEY);
            CREATE TABLE consent_row_filters (
                id TEXT PRIMARY KEY, dependent_table TEXT NULL, dependent_table_alias TEXT NULL, foreign_key_column TEXT NULL, primary_key_column TEXT NULL,
                subquery_predicate_json TEXT NULL, target_temporal_column TEXT NULL, dependent_valid_from_column TEXT NULL, dependent_valid_to_column TEXT NULL,
                target_table_alias TEXT NULL, additional_hops_json TEXT NULL, key_set_strategy INTEGER NULL);
            INSERT INTO consents VALUES ('active-role-consent', 'r', 'Analysts', 0), ('revoked-role-consent', 'r', 'Analysts', 1);
            """);

        var failure = await Should.ThrowAsync<PostgresException>(async () =>
        {
            foreach (var batch in step.Batches)
            {
                await Exec(conn, batch);
            }
        });
        failure.MessageText.ShouldContain("active-role-consent");
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'pre_step' AND table_name IN ('roles','role_members','access_policies','access_policy_grantees','access_policy_selectors')")).ShouldBe(5);
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'consent_row_filters' AND column_name = 'dependent_table'")).ShouldBe(1);

        // Once the consent is revoked the step applies and drops the dead objects but keeps the key set columns.
        await Exec(conn, "UPDATE consents SET is_revoked = 1");
        foreach (var batch in step.Batches)
        {
            await Exec(conn, batch);
        }

        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'pre_step' AND table_name IN ('roles','role_members','access_policies','access_policy_grantees','access_policy_selectors')")).ShouldBe(0);
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'consent_row_filters' AND column_name IN ('dependent_table','additional_hops_json')")).ShouldBe(0);
        (await Scalar(conn, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'pre_step' AND table_name = 'consent_row_filters' AND column_name = 'key_set_strategy'")).ShouldBe(1);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
