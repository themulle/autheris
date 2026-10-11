using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0e: the typed-policy services are registered once, as singletons.</summary>
public sealed class PolicyPredicateRegistrationTests
{
    [Fact]
    public void GovernanceRegistration_RegistersRendererParserAndRowPolicyFactory()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());
        services.AddSingleton<ISqlEngine, FastSqlEngine>();

        foreach (var service in new[] { typeof(IPolicyPredicateRenderer), typeof(IPolicyExpressionParser), typeof(RowPolicyFactory) })
        {
            services.Count(d => d.ServiceType == service).ShouldBe(1, service.Name);
            services.Single(d => d.ServiceType == service).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        }

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<RowPolicyFactory>();
        factory.CanParseAdminPredicates.ShouldBeTrue();
        provider.GetRequiredService<IPolicyPredicateRenderer>().ShouldBeOfType<PolicyPredicateRenderer>();
    }

    [Fact]
    public void GovernanceRegistration_RegistersTheGovernancePolicyFactoryOnce()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());
        services.AddSingleton<ISqlEngine, FastSqlEngine>();
        services.AddSingleton(NSubstitute.Substitute.For<Autheris.Application.Sql.Catalog.IGovernedCatalogProvider>());

        services.Count(d => d.ServiceType == typeof(GovernancePolicyFactory)).ShouldBe(1);
        services.Single(d => d.ServiceType == typeof(GovernancePolicyFactory)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<GovernancePolicyFactory>().ShouldNotBeNull();
    }
}
