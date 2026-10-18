using System.Data.Common;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PLAN-GOV-158-INC1 consent description persistence contract on SQLite.</summary>
public sealed class SqliteConsentDescriptionRepositoryContractTests : ConsentDescriptionRepositoryContractBase
{
    private readonly string _connectionString = $"Data Source=consentdesc_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
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
}
