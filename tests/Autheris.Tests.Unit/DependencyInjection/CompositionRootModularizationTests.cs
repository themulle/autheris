namespace Autheris.Tests.Unit.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Garnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CompositionRootModularizationTests
{
    private static (ServiceCollection Services, GatewayOptions Options, IHostEnvironment Env) CreateTestSetup(bool garnet = false)
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();

        var options = new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Garnet = new GarnetOptions
                {
                    EnableEmbeddedServer = garnet
                }
            },
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "Sqlite",
                ConnectionString = "Data Source=:memory:"
            }
        };

        services.AddSingleton(Options.Create(options));
        return (services, options, env);
    }

    [Fact]
    public void ModularExtensions_CanBeRegisteredIndividually()
    {
        var (services, options, env) = CreateTestSetup();

        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisMcp(options);
        services.AddAutherisCore(options, env);

        var sp = services.BuildServiceProvider();

        // Validate key registrations from different modules
        sp.GetService<TrinoSqlEngine.ISqlEngine>().ShouldNotBeNull();
        sp.GetService<IGovernanceRepository>().ShouldNotBeNull();
        sp.GetService<IRebacStore>().ShouldNotBeNull();
        sp.GetService<IRebacEvaluator>().ShouldNotBeNull();
        sp.GetService<ICompiledSqlQueryPlanCache>().ShouldNotBeNull();
        sp.GetService<IMcpToolRegistry>().ShouldNotBeNull();
    }

    [Fact]
    public void AddAutherisStorage_GarnetEmbedded_DoesNotStartServerDuringRegistration()
    {
        var (services, options, env) = CreateTestSetup(garnet: true);

        services.AddAutherisStorage(options, env);

        var sp = services.BuildServiceProvider();
        var garnetManager = sp.GetService<IGarnetServerManager>();
        garnetManager.ShouldNotBeNull();
        // Server should NOT be running yet; it must be started asynchronously by IHostedService
        garnetManager.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task RebacSeedHostedService_SeedsTuplesAndRecordsAudit()
    {
        var store = Substitute.For<IRebacStore>();
        var audit = Substitute.For<IAuditLogRepository>();
        var options = new GatewayOptions();
        options.Rebac.SeedTuples.Add(new RebacTuple("tenant-1", "user:alice", "reader", "table:lakehouse.dbo.sales"));

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        var hostedService = new RebacSeedHostedService(
            store,
            Options.Create(options),
            NullLogger<RebacSeedHostedService>.Instance,
            audit,
            env);

        await hostedService.StartAsync(CancellationToken.None);

        await store.Received(1).AddTuplesAsync(
            Arg.Is<IReadOnlyList<RebacTuple>>(list => Enumerable.Any(list, t => t.User == "user:alice")),
            Arg.Any<CancellationToken>());

        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(entry =>
                entry.EventType == "REBAC_TUPLE_SEED" &&
                entry.ActorSid.Value == "System:HostedService:RebacSeedHostedService" &&
                entry.Decision == "ALLOW"),
            Arg.Any<CancellationToken>());
    }
}
