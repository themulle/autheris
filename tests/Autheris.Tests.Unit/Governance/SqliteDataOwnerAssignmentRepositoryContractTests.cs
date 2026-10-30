using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #393 part b owner assignment contract on SQLite (see <see cref="DataOwnerAssignmentRepositoryContractTestsBase"/>).</summary>
public sealed class SqliteDataOwnerAssignmentRepositoryContractTests : DataOwnerAssignmentRepositoryContractTestsBase
{
    private readonly string _connectionString = $"Data Source=owners_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteGovernanceRepository? _repo;

    protected override Task<IGovernanceRepository?> CreateRepositoryAsync()
    {
        var options = Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = _connectionString, SeedDemoData = false } });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        return Task.FromResult<IGovernanceRepository?>(_repo);
    }

    protected override Task DisposeRepositoryAsync()
    {
        _repo?.Dispose();
        return Task.CompletedTask;
    }

    protected override DbConnection OpenRaw()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ManualOrSeedPath_NeverEmitsDbtPrefix()
    {
        // I393B-T-10 / SC-07: the demo seed writes TABLE_OWNERS ids that never carry the dbt: provenance prefix.
        var options = Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=owners_seed_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", SeedDemoData = true } });
        using var seeded = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);

        var conn = seeded.RawConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TABLE_OWNERS";
        Convert.ToInt32(cmd.ExecuteScalar()).ShouldBeGreaterThan(0);
        cmd.CommandText = "SELECT COUNT(*) FROM TABLE_OWNERS WHERE id LIKE 'dbt:%'";
        Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(0);
    }
}
