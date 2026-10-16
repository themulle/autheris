using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-SC-01 / SEC-SC-02: the consent query is served by indexes, and the supporting indexes exist in the baseline.</summary>
[Trait("Category", "Security")]
public sealed class ConsentIndexUsageTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repo = GovernanceStoreFactory.CreateSqlite(new EpochValidationService());

    public void Dispose() => _repo.Dispose();

    private async Task<List<string>> PlanAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await _repo.Connections.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(3));
        }

        return rows;
    }

    [Theory]
    [InlineData("CONSENTS", "ix_consents_tenant_table", "tenant_id,table_id,is_revoked")]
    [InlineData("CONSENTS", "ix_consents_tenant_grantee", "tenant_id,grantee_sid_norm,is_revoked")]
    [InlineData("CONSENTS", "ix_consents_tenant_role", "tenant_id,role_name")]
    [InlineData("CONSENT_COLUMN_RULES", "ix_consent_column_rules_consent", "consent_id")]
    [InlineData("CONSENT_ROW_FILTERS", "ix_consent_row_filters_consent", "consent_id")]
    [InlineData("COLUMN_MASKING_RULES", "ix_column_masking_rules_column", "table_column_id")]
    [InlineData("ROLE_MEMBERS", "ix_role_members_member", "member_sid")]
    [InlineData("TABLE_OWNERS", "ix_table_owners_owner", "data_owner_id")]
    [InlineData("TABLES", "ix_tables_natural_ci", "source_name,schema_name,table_name")]
    public async Task Baseline_CreatesSupportingIndex(string table, string index, string columns)
    {
        await using var conn = await _repo.Connections.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_index_info('{index}') ORDER BY seqno";
        var found = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                found.Add(reader.GetString(0));
            }
        }

        string.Join(',', found).ShouldBe(columns, $"index {index} on {table}");
    }

    [Fact]
    public async Task SubjectConsentQuery_UsesIndexesOnly_NoTableScanOfConsents()
    {
        var sql = SqliteGovernanceRepository.BuildSubjectConsentSql(subjectCount: 3, withTenant: true);

        var plan = await PlanAsync(
            sql,
            ("@domain", "a"), ("@schema", "b"), ("@table", "c"), ("@tenantId", "t"),
            ("@s0", "X"), ("@s1", "Y"), ("@s2", "Z"));

        plan.ShouldNotContain(p => p.StartsWith("SCAN c", StringComparison.Ordinal) || p.StartsWith("SCAN CONSENTS", StringComparison.Ordinal),
            string.Join(" | ", plan));
        plan.ShouldNotContain(p => p.StartsWith("SCAN t", StringComparison.Ordinal) || p.StartsWith("SCAN TABLES", StringComparison.Ordinal),
            string.Join(" | ", plan));
    }

    [Fact]
    public void SubjectConsentSql_AlwaysCarriesTenantPredicate_WhenTenantGiven()
    {
        SqliteGovernanceRepository.BuildSubjectConsentSql(1, withTenant: true).ShouldContain("c.tenant_id = @tenantId");
        SqliteGovernanceRepository.BuildSubjectConsentSql(1, withTenant: false).ShouldNotContain("c.tenant_id = @tenantId");
    }
}
