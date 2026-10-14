namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>WP-ARC-A-03: PostgreSQL contract tests of the CloudEvent subscription store. Need Docker; without it they return early.</summary>
public sealed class PostgreSqlCloudEventSubscriptionStoreContractTests : CloudEventSubscriptionStoreContractTests
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

    protected override Task<ICloudEventSubscriptionStore?> OpenAsync()
    {
        if (_db is not { IsAvailable: true })
        {
            return Task.FromResult<ICloudEventSubscriptionStore?>(null);
        }

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _db.ConnectionString,
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        });
        var repo = Track(GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env));
        return Task.FromResult<ICloudEventSubscriptionStore?>(repo);
    }
}
