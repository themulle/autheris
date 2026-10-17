namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>T-W0-9 (W0-C5): fail-closed wiring of the catalog discovery service from the real registration.</summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryWiringTests
{
    private static ServiceCollection Services(bool withConsentRepository, GatewayOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Wp0.Tables(
            Wp0.Table("sales", "orders_a", "Orders", ["id"]),
            Wp0.Table("sales", "invoices_b", "Invoices", ["id"])));
        services.AddSingleton(Substitute.For<IAuditLogRepository>());
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(options ?? Wp0.Options(TenancyMode.Multi)));
        if (withConsentRepository)
        {
            services.AddSingleton(Wp0.Consents(Wp0.Allow(new Autheris.Domain.Common.TableIdentifier("sales", "dbo", "orders_a"), "S-A-USER", Wp0.TenantA)));
        }

        services.AddAutherisCatalog();
        return services;
    }

    [Fact]
    public async Task RealRegistration_ConsentlessCaller_SeesNothing_ConsentedCallerSeesOnlyOwnTable()
    {
        using var provider = Services(withConsentRepository: true).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var discovery = scope.ServiceProvider.GetRequiredService<ICatalogDiscoveryService>();

        discovery.ShouldBeOfType<CatalogDiscoveryService>();
        (await discovery.ListDatasetsAsync(Wp0.Context(Wp0.User("S-B-USER", "tenant-b")))).ShouldBeEmpty();
        (await discovery.ListDatasetsAsync(Wp0.Context(Wp0.User("S-A-USER", "tenant-a")))).ShouldHaveSingleItem().Table.ShouldBe("orders_a");
        (await discovery.ListDatasetsAsync(new Autheris.Domain.Model.RequestContext())).ShouldBeEmpty();
    }

    [Fact]
    public void RealRegistration_WithoutConsentRepository_FailsClosedAtResolution()
    {
        using var provider = Services(withConsentRepository: false).BuildServiceProvider();
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ICatalogDiscoveryService>());
    }

    [Fact]
    public void Constructor_RequiresConsentRepositoryAndOptions()
    {
        var tables = Wp0.Tables();
        var audit = Substitute.For<IAuditLogRepository>();

        Should.Throw<ArgumentNullException>(() => new CatalogDiscoveryService(tables, audit, null!, Options.Create(new GatewayOptions())));
        Should.Throw<ArgumentNullException>(() => new CatalogDiscoveryService(tables, audit, Wp0.Consents(), null!));
    }
}
