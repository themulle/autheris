using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>WP-SC-01 / SEC-SC-02: the supporting indexes of the consent query exist in the PostgreSQL baseline.</summary>
[Trait("Category", "Security")]
public sealed class ConsentIndexUsageTests : IDisposable
{
    private readonly PostgreSqlGovernanceRepository _repo;
    private readonly string _connectionString;

    public ConsentIndexUsageTests() => _repo = GovernanceTestDatabase.CreateRepository(new EpochValidationService(), out _connectionString);

    public void Dispose() => _repo.Dispose();

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
    public void Baseline_CreatesSupportingIndex(string table, string index, string columns)
    {
        var definition = GovernanceTestDatabase.ScalarRaw(
            _connectionString,
            "SELECT indexdef FROM pg_indexes WHERE indexname = @index AND tablename = lower(@table)",
            ("@index", index), ("@table", table)) as string;

        definition.ShouldNotBeNull($"index {index} on {table} must exist");
        var position = 0;
        foreach (var column in columns.Split(','))
        {
            var at = definition.IndexOf(column, position, StringComparison.Ordinal);
            at.ShouldBeGreaterThanOrEqualTo(0, $"index {index} on {table} must cover {column} in order: {definition}");
            position = at + column.Length;
        }
    }
}
