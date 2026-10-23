namespace Autheris.Tests.Integration;

using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>PERS-17 metadata cache replica contract on SqlServer.</summary>
public sealed class SqlServerMetadataCacheReplicaContractTests : MetadataCacheReplicaContractBase
{
    private SqlServerTestDatabase? _db;
    private SqlServerGovernanceRepository? _a;
    private SqlServerGovernanceRepository? _b;

    private SqlServerGovernanceRepository Create()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "SqlServer", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        });
        return GovernanceStoreFactory.CreateSqlServer(Substitute.For<IEpochValidationService>(), options, env);
    }

    protected override async Task<(ITableMetadataRepository A, ITableMetadataRepository B)?> CreateReplicasAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return null;
        }

        _a = Create();
        _b = Create();
        return (_a, _b);
    }

    protected override async Task DisposeReplicasAsync()
    {
        if (_a != null)
        {
            await _a.DisposeAsync();
        }

        if (_b != null)
        {
            await _b.DisposeAsync();
        }

        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }
}
