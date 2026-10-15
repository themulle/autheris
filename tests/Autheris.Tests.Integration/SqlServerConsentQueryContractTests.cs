namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-SC-01 on SQL Server: result parity of the indexed consent query, chunking and baseline indexes.</summary>
public sealed class SqlServerConsentQueryContractTests : ConsentQueryContractTestsBase
{
    private SqlServerTestDatabase? _db;
    private SqlServerGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "SqlServer", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false
            }
        });
        _repo = GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
        return _repo;
    }

    protected override async Task DisposeRepositoryAsync()
    {
        if (_repo != null)
        {
            await _repo.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("ix_consents_tenant_table", "CONSENTS")]
    [InlineData("ix_consents_tenant_grantee", "CONSENTS")]
    [InlineData("ix_consents_tenant_role", "CONSENTS")]
    [InlineData("ix_consent_column_rules_consent", "CONSENT_COLUMN_RULES")]
    [InlineData("ix_consent_row_filters_consent", "CONSENT_ROW_FILTERS")]
    [InlineData("ix_column_masking_rules_column", "COLUMN_MASKING_RULES")]
    [InlineData("ix_role_members_member", "ROLE_MEMBERS")]
    [InlineData("ix_table_owners_owner", "TABLE_OWNERS")]
    [InlineData("ix_tables_natural_ci", "TABLES")]
    public async Task SEC_SC_02_Baseline_CreatesIndex(string index, string table)
    {
        if (!Available) return;
        await using var conn = new SqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.indexes WHERE name = @n AND object_id = OBJECT_ID(@t)", conn);
        cmd.Parameters.AddWithValue("@n", index);
        cmd.Parameters.AddWithValue("@t", "dbo." + table);

        Convert.ToInt64(await cmd.ExecuteScalarAsync()).ShouldBe(1);
    }
}
