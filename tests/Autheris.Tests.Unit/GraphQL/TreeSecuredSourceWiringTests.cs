using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA06F-C8: the secured-source builder is registered wherever <c>GovernedTreeQueryService</c> is, with the governance factory and the engine
/// of the same container. Without that wiring a subquery policy stays denied and the GraphQL channel parity tests stay red.
/// </summary>
public sealed class TreeSecuredSourceWiringTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task SQLA06F_AutherisExecution_RegistersABuilderThatCanBuildASecuredSource()
    {
        using var harness = new SecuredTreeHarness();
        var options = new GatewayOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutherisGovernance(options);
        services.AddAutherisExecution(options);
        services.AddSingleton<ISqlEngine>(harness.Engine);
        services.AddSingleton(harness.CatalogProvider);
        services.AddSingleton(harness.MetadataRepository);
        services.AddSingleton(Substitute.For<IEpochValidationService>());
        services.AddSingleton(Substitute.For<ISqlConnectionFactory>());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        services.Count(d => d.ServiceType == typeof(TreeSecuredSourceBuilder)).ShouldBe(1);
        services.Single(d => d.ServiceType == typeof(TreeSecuredSourceBuilder)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();
        var builder = provider.GetRequiredService<TreeSecuredSourceBuilder>();

        var access = SecuredTreeHarness.Resolved(
            SecuredTreeHarness.Meta(SecuredTreeHarness.OrdersId),
            SecuredTreeHarness.Decision(SecuredTreeHarness.OrdersId, SecuredTreeHarness.ExistsPolicy()),
            "t1");
        var source = await builder.BuildAsync(access, SecuredTreeHarness.DataSource, DatabaseDialect.Sqlite, CancellationToken.None);

        source.Sql.ShouldContain("FROM");
    }
}
