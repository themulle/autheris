namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Security.Cryptography;
using Autheris.Api.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>Module service guard and configuration claim rules (WP-SIMPL-50 B12, C12).</summary>
public sealed partial class ModuleLoaderSecurityTests
{
    private void AssertModuleRejected(string moduleId, Type entryType, string reasonFragment)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, moduleId, entryType);

        var services = new ServiceCollection();
        services.AddAuthorization();
        var config = BuildConfig(moduleId);
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe(moduleId);
        ex.Reason.ShouldContain(reasonFragment);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_registering_startup_filter_is_rejected() =>
        AssertModuleRejected("evil-startup", typeof(StartupFilterAddingModule), "IStartupFilter");

    [Fact]
    [Trait("Category", "Security")]
    public void Module_registering_hosted_service_is_rejected() =>
        AssertModuleRejected("evil-hosted", typeof(HostedServiceAddingModule), "IHostedService");

    [Fact]
    [Trait("Category", "Security")]
    public void Module_configuring_authorization_options_is_rejected() =>
        AssertModuleRejected("evil-authz-options", typeof(AuthorizationOptionsConfiguringModule), "AuthorizationOptions");

    [Fact]
    [Trait("Category", "Security")]
    public void Module_post_configuring_jwt_options_is_rejected() =>
        AssertModuleRejected("evil-jwt-options", typeof(JwtOptionsPostConfiguringModule), "JwtBearerOptions");

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-11")]
    public void Module_replacing_client_ip_resolver_is_rejected() =>
        AssertModuleRejected("evil-client-ip", typeof(ClientIpResolverReplacingModule), "IClientIpResolver");

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-11")]
    public void Module_configuring_data_protection_is_rejected() =>
        AssertModuleRejected("evil-data-protection", typeof(DataProtectionConfiguringModule), "DataProtectionOptions");

    [Fact]
    [Trait("Category", "Security")]
    public void Module_claiming_foreign_section_is_rejected()
    {
        AssertModuleRejected("claim-foreign", typeof(ForeignSectionClaimingModule), "ConnectionStrings");
        AssertModuleRejected("claim-gateway", typeof(ForeignGatewaySectionClaimingModule), "Gateway:Caching");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-12")]
    public void Module_id_colliding_with_core_section_is_rejected() =>
        AssertModuleRejected("caching", typeof(CoreSectionIdModule), "Gateway:Caching");
}
