namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 3: every place that creates the access decision gets the real resolver from DI. A decision
/// point without it would silently skip all virtual filters (the plan's main risk).
/// </summary>
public sealed class VirtualFilterWiringTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = $"Data Source=vf_wiring_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" }
        };
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        services.AddSingleton(env);
        services.AddSingleton(Options.Create(options));
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddGatewayInfrastructure(options);
        return services.BuildServiceProvider();
    }

    private static object? ResolverField(object service) =>
        service.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(IMandatoryRowFilterResolver)).GetValue(service);

    [Theory]
    [InlineData(typeof(IGatewayExecutionService))]
    [InlineData(typeof(IGovernedSqlExecutionService))]
    [InlineData(typeof(IProcedureExecutionService))]
    [InlineData(typeof(IUnifiedPolicyDecisionPoint))]
    [InlineData(typeof(Autheris.Application.Streaming.Interfaces.IStreamRlsPolicyEnforcer))]
    public async Task DecisionPoints_GetTheRealResolver(Type serviceType)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService(serviceType);

        ResolverField(service).ShouldBeOfType<MandatoryRowFilterResolver>();
    }

    [Fact]
    public async Task CrossDomainResolver_PolicyGetsTheRealResolver()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ICrossDomainAccessResolver>();

        var policy = resolver.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(TableAccessPolicy)).GetValue(resolver)!;

        ResolverField(policy).ShouldBeOfType<MandatoryRowFilterResolver>();
    }
}
