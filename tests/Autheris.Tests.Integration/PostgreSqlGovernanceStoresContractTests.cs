namespace Autheris.Tests.Integration;

using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

/// <summary>PERS-16 / PERS-07 / PERS-09 / GOV-02 store contract (identity, functional rights, recertification) on PostgreSql.</summary>
[Trait("Category", "Security")]
public sealed class PostgreSqlGovernanceStoresContractTests : GovernanceStoresContractBase
{
    private PostgreSqlTestDatabase? _db;
    private PostgreSqlGovernanceRepository? _repo;

    protected override async Task<object?> CreateRepositoryAsync()
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
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
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
}
