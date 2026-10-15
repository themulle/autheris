namespace Autheris.Tests.Integration;

using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.AccessProfiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>WP-SC-03 / SEC-SC-04 contract of the persisted access profiles on PostgreSql, including a second gateway replica on the same database.</summary>
public sealed class PostgreSqlAccessProfileRepositoryContractTests : AccessProfileRepositoryContractBase
{
    private PostgreSqlTestDatabase? _db;
    private PostgreSqlGovernanceRepository? _repo;
    private PostgreSqlGovernanceRepository? _second;

    private PostgreSqlGovernanceRepository Create()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
    }

    protected override async Task<IAccessProfileRepository?> CreateRepositoryAsync()
    {
        _db = await PostgreSqlTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        _repo = Create();
        return _repo;
    }

    protected override IAccessProfileRepository? CreateSecondInstance() => _second ??= Create();

    protected override async Task DisposeRepositoryAsync()
    {
        if (_second != null)
        {
            await _second.DisposeAsync();
        }

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
