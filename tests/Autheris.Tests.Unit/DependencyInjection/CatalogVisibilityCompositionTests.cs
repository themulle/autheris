namespace Autheris.Tests.Unit.DependencyInjection;

using System.Reflection;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#138 INC-1 P3: the composition root hands the one <see cref="ICatalogVisibilityService"/> to every discovery consumer.</summary>
public sealed class CatalogVisibilityCompositionTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" }
        };
        services.AddSingleton(Options.Create(options));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisCatalog();
        services.AddAutherisMcp(options);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ICatalogVisibilityService? Injected(object consumer) =>
        consumer.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => typeof(ICatalogVisibilityService).IsAssignableFrom(f.FieldType))
            .Select(f => (ICatalogVisibilityService?)f.GetValue(consumer))
            .FirstOrDefault();

    [Fact]
    public async Task CatalogVisibilityService_ResolvesForAllDiscoveryConsumers()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<ICatalogVisibilityService>();
        service.ShouldBeOfType<CatalogVisibilityService>();

        var rest = scope.ServiceProvider.GetRequiredService<ICatalogDiscoveryService>();
        var mcp = scope.ServiceProvider.GetRequiredService<IMcpDatasetCatalog>();
        Injected(rest).ShouldBeSameAs(service);
        Injected(mcp).ShouldBeSameAs(service);
    }

    [Fact]
    public async Task GraphQl_CatalogField_ReceivesTheDiResolvedService_NotSilentlyEmpty()
    {
        var table = Wp0.Table("sales", "orders", "Orders", ["id"]) with { CatalogView = CatalogView.Granted };
        var options = new GatewayOptions { Rebac = new RebacOptions { Enabled = false } };
        var user = Wp0.User("S-USER", "tenant-a");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<IAuditLogRepository>());
        services.AddSingleton(Wp0.Tables(table));
        services.AddSingleton(Wp0.Consents(Wp0.Allow(table.Identifier, "S-USER", Wp0.TenantA, "id")));
        services.AddSingleton(Substitute.For<IConsentApprovalRepository>());
        services.AddSingleton(Substitute.For<IDataOwnershipRepository>());
        services.AddSingleton(accessor);
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(options));
        services.AddAutherisCatalog();
        services.AddGraphQLServer().AddQueryType<Query>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var executor = await provider.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var result = await executor.ExecuteAsync("{ catalog { tableName } }");

        var operation = result.ExpectOperationResult();
        (operation.Errors ?? []).ShouldBeEmpty(operation.Errors?.FirstOrDefault()?.Message);
        result.ToJson().ShouldContain("orders");
    }
}
