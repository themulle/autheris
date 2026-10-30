namespace Autheris.Tests.Unit.DependencyInjection;

using System.Security.Claims;
using Autheris.Api.Extensions;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// DT-A-10 / DT-A-11: the composition root fills the realm registry from <c>Authentication:Realms</c>. Without that call no realm is
/// registered and every realm-bound principal resolves Invalid (fail closed, but unusable).
/// </summary>
[Trait("Category", "Security")]
public sealed class IdentityRealmRegistryWiringTests
{
    private const string DefaultTenant = "default-tenant";

    private static ServiceProvider Build(IdentityRealmsOptions realms)
    {
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        var options = new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" },
            Tenancy = new TenancyOptions { DefaultTenantId = DefaultTenant }
        };
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IOptionsMonitor<GatewayOptions>>(new Autheris.Tests.Common.StaticOptionsMonitor<GatewayOptions>(options));
        services.AddSingleton(Options.Create(realms));
        services.AddAutherisStorage(options, env);
        services.AddAutherisGovernance(options);
        services.AddAutherisSecurity(options, env);
        services.AddAutherisSqlEngine(options);
        services.AddAutherisExecution(options, env);
        services.AddAutherisCatalog();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IdentityRealmsOptions Realms()
    {
        var realms = new IdentityRealmsOptions();
        realms.Realms["fixed"] = new IdentityRealmConfigOptions { Kind = IdentityRealmKind.Basic, FixedTenantId = "tenant-fixed" };
        realms.Realms["unbound"] = new IdentityRealmConfigOptions { Kind = IdentityRealmKind.Basic };
        realms.Realms["scim-realm"] = new IdentityRealmConfigOptions
        {
            Kind = IdentityRealmKind.Basic,
            Provisioning = RealmProvisioningMode.Scim,
            FixedTenantId = "tenant-scim"
        };
        return realms;
    }

    private static ClaimsPrincipal RealmPrincipal(string realmId) =>
        new(new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("autheris:realm", realmId)], "Test"));

    [Fact]
    public void Configured_Realms_Are_Registered_By_The_Composition_Root()
    {
        using var provider = Build(Realms());

        var registry = provider.GetRequiredService<IIdentityRealmRegistry>();

        registry.GetRealm("fixed").ShouldNotBeNull();
        registry.GetRealm("unbound").ShouldNotBeNull();
        registry.GetAllRealms().Count.ShouldBe(3);
    }

    [Fact]
    public void Fixed_Tenant_Realm_Resolves_To_Its_Tenant_Through_The_TenantResolver()
    {
        using var provider = Build(Realms());

        var resolution = provider.GetRequiredService<ITenantResolver>().Resolve(RealmPrincipal("fixed"));

        resolution.Outcome.ShouldBe(TenantResolutionOutcome.FromRealm);
        resolution.Tenant!.Value.Value.ShouldBe("tenant-fixed");
    }

    [Fact]
    public void Unbound_Realm_Resolves_To_The_Default_Tenant()
    {
        using var provider = Build(Realms());

        var resolution = provider.GetRequiredService<ITenantResolver>().Resolve(RealmPrincipal("unbound"));

        resolution.Outcome.ShouldBe(TenantResolutionOutcome.DefaultAssigned);
        resolution.Tenant!.Value.Value.ShouldBe(DefaultTenant);
    }

    [Fact]
    public void Scim_Realm_Principal_Resolves_Its_Tenant()
    {
        using var provider = Build(Realms());

        var resolution = provider.GetRequiredService<ITenantResolver>().Resolve(RealmPrincipal("scim-realm"));

        resolution.Outcome.ShouldBe(TenantResolutionOutcome.FromRealm);
        resolution.Tenant!.Value.Value.ShouldBe("tenant-scim");
    }

    [Fact]
    public void Unregistered_Realm_Stays_Invalid_Fail_Closed()
    {
        using var provider = Build(Realms());

        provider.GetRequiredService<ITenantResolver>().Resolve(RealmPrincipal("nope")).Outcome
            .ShouldBe(TenantResolutionOutcome.Invalid);
    }
}
