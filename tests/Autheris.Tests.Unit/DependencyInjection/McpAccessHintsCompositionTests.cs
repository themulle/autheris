namespace Autheris.Tests.Unit.DependencyInjection;

using System.Reflection;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Production registration hands the catalog the read-path switches, so its hints are not silently dropped.</summary>
public sealed class McpAccessHintsCompositionTests
{
    [Fact]
    public async Task Production_registration_resolves_catalog_hints_with_switches()
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = Autheris.Tests.Common.Governance.GovernanceTestConnectionStrings.NonConnectingPostgreSql }
        };
        services.AddSingleton(Options.Create(options));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisCatalog();
        services.AddAutherisMcp(options);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IReadPathSwitches>().ShouldNotBeNull();
        var catalog = scope.ServiceProvider.GetRequiredService<IMcpDatasetCatalog>();
        var hints = catalog.GetType().GetField("_hints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        hints.GetType().GetMethod("Guidance")!.Invoke(hints, null).ShouldNotBeNull();
    }
}
