namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-SC-01 on PostgreSQL: result parity of the indexed consent query and index usage.</summary>
public sealed class PostgreSqlConsentQueryContractTests : ConsentQueryContractTestsBase
{
    private PostgreSqlTestDatabase? _db;
    private PostgreSqlGovernanceRepository? _repo;

    protected override async Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
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
                Provider = "PostgreSql", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false
            }
        });
        _repo = GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
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

    [Fact]
    public async Task SEC_SC_02_ConsentQuery_IsServedByTheBaselineIndexes()
    {
        if (!Available) return;
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", conn))
        {
            await off.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "EXPLAIN SELECT c.id FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id " +
            "WHERE LOWER(t.source_name) = LOWER(@d) AND LOWER(t.schema_name) = LOWER(@s) AND LOWER(t.table_name) = LOWER(@t) " +
            "AND c.is_revoked = 0 AND c.tenant_id = @tenant" + PostgreSqlGovernanceRepository.SubjectPredicate, conn);
        cmd.Parameters.AddWithValue("@d", "a");
        cmd.Parameters.AddWithValue("@s", "b");
        cmd.Parameters.AddWithValue("@t", "c");
        cmd.Parameters.AddWithValue("@tenant", "x");
        cmd.Parameters.AddWithValue("@subjects", new[] { "X" });
        var plan = new System.Text.StringBuilder();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        var text = plan.ToString();
        text.ShouldContain("ix_tables_natural_ci");
        text.ShouldContain("ix_consents_tenant_table");
    }

    [Theory]
    [InlineData("ix_consents_tenant_table")]
    [InlineData("ix_consents_tenant_grantee")]
    [InlineData("ix_consents_tenant_role")]
    [InlineData("ix_consent_column_rules_consent")]
    [InlineData("ix_consent_row_filters_consent")]
    [InlineData("ix_column_masking_rules_column")]
    [InlineData("ix_role_members_member")]
    [InlineData("ix_table_owners_owner")]
    [InlineData("ix_tables_natural_ci")]
    public async Task SEC_SC_02_Baseline_CreatesIndex(string index)
    {
        if (!Available) return;
        await using var conn = new NpgsqlConnection(_db!.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname = @n", conn);
        cmd.Parameters.AddWithValue("@n", index);

        Convert.ToInt64(await cmd.ExecuteScalarAsync()).ShouldBe(1);
    }
}
