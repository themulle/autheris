namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Webhooks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

/// <summary>WP-ARC-A-02: PostgreSQL contract tests of the webhook outbox. Need Docker; without it they return early.</summary>
public sealed class PostgreSqlWebhookOutboxContractTests : WebhookOutboxContractTests
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

    protected override Task<IWebhookOutbox?> OpenAsync()
    {
        if (_db is not { IsAvailable: true })
        {
            return Task.FromResult<IWebhookOutbox?>(null);
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
        return Task.FromResult<IWebhookOutbox?>(repo);
    }
}
