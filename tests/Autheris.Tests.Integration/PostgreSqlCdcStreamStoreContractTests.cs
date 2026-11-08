namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Cdc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>PLAN-CDC-99 WP-I1-A: the PostgreSQL governance repository satisfies the shared stream/key store contract (replaces the SQLite twin).</summary>
public sealed class PostgreSqlCdcStreamStoreContractTests : CdcStreamStoreContractTests
{
    private PostgreSqlTestDatabase? _db;

    public override async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
        }
        catch (Exception ex)
        {
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    protected override Task<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?> OpenAsync()
    {
        if (_db is not { IsAvailable: true })
        {
            return Task.FromResult<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?>(null);
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db.ConnectionString, SeedDemoData = false }
        });
        var repository = Track(GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env));
        return Task.FromResult<(ICdcStreamStore Streams, ICdcStreamKeyStore Keys)?>((repository, repository));
    }
}
