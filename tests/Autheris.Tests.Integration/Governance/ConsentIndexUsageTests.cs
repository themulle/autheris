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

    [Fact]
    public async Task SubjectConsentQuery_WithTenant_NeverReturnsAnotherTenantsConsents()
    {
        // WP-SC-01 / SEC-SC-02: behavioural replacement of the former SQLite tenant-predicate check.
        var table = new Autheris.Domain.Common.TableIdentifier("sales", "dbo", "tenant_iso");
        var meta = await _repo.UpsertTableMetadataAsync(new Autheris.Domain.Model.TableMetadata
        {
            Identifier = table,
            Table = new Autheris.Domain.Model.Table { SourceType = "sql", SourceName = "sales", SchemaName = "dbo", TableName = "tenant_iso", DisplayName = "tenant_iso" },
            Columns = [new Autheris.Domain.Model.TableColumn { ColumnName = "a", DataType = "integer" }]
        });
        var subject = new Autheris.Domain.Common.Sid("S-1-5-21-TENANT-ISO");
        var now = TimeProvider.System.GetUtcNow();
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            await _repo.CreateConsentAsync(new Autheris.Domain.Model.Consent
            {
                TenantId = new Autheris.Domain.Common.TenantId(tenant),
                TableId = meta.Table.Id,
                TableIdentifier = table,
                Effect = Autheris.Domain.Model.ConsentEffect.Allow,
                GranteeType = Autheris.Domain.Model.GranteeType.User,
                GranteeSid = subject,
                ValidFrom = now.AddMinutes(-5),
                ValidTo = now.AddDays(1)
            });
        }

        var forA = await _repo.GetActiveConsentsForSubjectsAsync([subject], table, now, new Autheris.Domain.Common.TenantId("tenant-a"));

        forA.ShouldHaveSingleItem().TenantId.Value.ShouldBe("tenant-a");
    }
}
