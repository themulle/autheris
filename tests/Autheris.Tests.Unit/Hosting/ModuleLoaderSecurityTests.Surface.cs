namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Security.Cryptography;
using Autheris.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

/// <summary>Module service guard and configuration claim rules (WP-SIMPL-50 B12, C12).</summary>
[Trait("Category", "Security")]
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
    public void Second_dbt_webhook_handler_fails_startup() =>
        AssertModuleRejected("evil-dbt-webhook", typeof(DbtWebhookHandlerAddingModule), "IDbtCloudWebhookHandler");

    [Fact]
    [Trait("Category", "Security")]
    public void Two_dbt_webhook_handler_descriptors_fail_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, DbtWebhookHandlerAddingModule.ForeignHandler>();
        services.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, DbtWebhookHandlerAddingModule.ForeignHandler>();

        var ex = Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyDbtCloudWebhookHandler("dbt", services));
        ex.Reason.ShouldContain("IDbtCloudWebhookHandler");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dbt_webhook_handler_from_another_module_id_fails_startup()
    {
        var services = new ServiceCollection();
        var before = ModuleSecurityGuards.CaptureDbtCloudWebhookHandlers(services);
        services.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, Autheris.Modules.Dbt.DbtWebhookReceiver>();

        var ex = Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyDbtCloudWebhookHandler("other", services, before));
        ex.Reason.ShouldContain("only module 'dbt'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Late_added_dbt_webhook_handler_fails_final_check()
    {
        var services = new ServiceCollection();
        services.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, Autheris.Modules.Dbt.DbtWebhookReceiver>();

        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalDbtCloudWebhookHandler(services, ["lakehouse"]));
        Should.NotThrow(() => ModuleSecurityGuards.VerifyFinalDbtCloudWebhookHandler(services, ["dbt"]));

        services.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, Autheris.Modules.Dbt.DbtWebhookReceiver>();
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalDbtCloudWebhookHandler(services, ["dbt"]));

        var foreign = new ServiceCollection();
        foreign.AddScoped<Autheris.Application.Catalog.Semantic.IDbtCloudWebhookHandler, DbtWebhookHandlerAddingModule.ForeignHandler>();
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalDbtCloudWebhookHandler(foreign, ["dbt"]));
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
    public void Module_configuring_forwarded_headers_is_rejected() =>
        AssertModuleRejected("evil-forwarded", typeof(ForwardedHeadersConfiguringModule), "ForwardedHeadersOptions");

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("bad:id")]
    [InlineData("Upper")]
    [InlineData("a b")]
    [InlineData("-lead")]
    [InlineData("trail-")]
    [InlineData("dou--ble")]
    public void Module_id_outside_the_allowed_pattern_is_rejected(string moduleId)
    {
        var config = BuildConfig(moduleId);
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };
        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);

        Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(new ServiceCollection(), config, env));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Gateway:Caching", true)]
    [InlineData("Gateway:Caching:Foo", true)]
    [InlineData("Gateway:Lakehouse", false)]
    [InlineData("Gateway:Sample", false)]
    public void Host_config_section_check_covers_parent_prefixes(string section, bool expected) =>
        ModuleSecurityGuards.IsHostConfigSection(section).ShouldBe(expected);

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

    [Fact]
    [Trait("Category", "Security")]
    public void Module_httpclient_without_ssrf_handler_fails_startup()
    {
        AssertModuleRejected("evil-httpclient", typeof(UnprotectedHttpClientAddingModule), "SSRF");
        AssertModuleRejected("evil-httpclient-defaults", typeof(HttpClientDefaultsAddingModule), "HttpClient");
        AssertModuleRejected("evil-handler-filter", typeof(HandlerBuilderFilterAddingModule), "IHttpMessageHandlerBuilderFilter");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CreateSignedModuleForEntryType(key, "good-httpclient", typeof(ProtectedHttpClientAddingModule));

        var services = new ServiceCollection();
        services.AddAuthorization();
        var config = BuildConfig("good-httpclient");
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(publicKeyOverride: key, isDirectoryWritableOverride: _ => false);
        loader.LoadAndConfigureServices(services, config, env).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_port_from_other_module_fails_startup()
    {
        AssertModuleRejected("evil-om-webhook", typeof(OpenMetadataWebhookHandlerAddingModule), "IOpenMetadataWebhookHandler");
        AssertModuleRejected("evil-om-sync", typeof(OpenMetadataSyncServiceAddingModule), "IOpenMetadataSyncService");
        AssertModuleRejected("evil-om-catalog", typeof(OpenMetadataCatalogClientAddingModule), "IDataCatalogClient");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Second_openmetadata_webhook_handler_fails_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataWebhookHandler, Autheris.Modules.OpenMetadata.OpenMetadataSyncService>();
        services.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataWebhookHandler, Autheris.Modules.OpenMetadata.OpenMetadataSyncService>();

        var ex = Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyModuleOwnedPorts("openmetadata", services));
        ex.Reason.ShouldContain("IOpenMetadataWebhookHandler");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Catalog_enabled_without_openmetadata_module_fails_startup()
    {
        var services = new ServiceCollection();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Catalog:Enabled"] = "true"
            })
            .Build();
        var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };

        var loader = new ModuleLoader(isDirectoryWritableOverride: _ => false);
        var ex = Should.Throw<ModuleLoadException>(() => loader.LoadAndConfigureServices(services, config, env));
        ex.ModuleId.ShouldBe("host");
        ex.Reason.ShouldContain("openmetadata");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_port_with_factory_registration_fails_startup()
    {
        var webhookServices = new ServiceCollection();
        webhookServices.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataWebhookHandler>(_ => new OpenMetadataWebhookHandlerAddingModule.ForeignWebhookHandler());
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyModuleOwnedPorts("openmetadata", webhookServices))
            .Reason.ShouldContain("ImplementationType");

        var syncServices = new ServiceCollection();
        syncServices.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataSyncService>(_ => new OpenMetadataSyncServiceAddingModule.ForeignSyncService());
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyModuleOwnedPorts("openmetadata", syncServices))
            .Reason.ShouldContain("ImplementationType");

        var catalogServices = new ServiceCollection();
        catalogServices.AddTransient<Autheris.Application.Catalog.Interfaces.IDataCatalogClient>(_ => new OpenMetadataCatalogClientAddingModule.ForeignCatalogClient());
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyModuleOwnedPorts("openmetadata", catalogServices))
            .Reason.ShouldContain("ImplementationType");

        var instanceServices = new ServiceCollection();
        instanceServices.AddSingleton<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataWebhookHandler>(new OpenMetadataWebhookHandlerAddingModule.ForeignWebhookHandler());
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyModuleOwnedPorts("openmetadata", instanceServices))
            .Reason.ShouldContain("ImplementationType");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_port_without_loaded_module_fails_startup()
    {
        var webhookServices = new ServiceCollection();
        webhookServices.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataWebhookHandler, Autheris.Modules.OpenMetadata.OpenMetadataSyncService>();
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(webhookServices, []));
        Should.NotThrow(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(webhookServices, ["openmetadata"]));

        var syncServices = new ServiceCollection();
        syncServices.AddScoped<Autheris.Application.Catalog.OpenMetadata.IOpenMetadataSyncService, Autheris.Modules.OpenMetadata.OpenMetadataSyncService>();
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(syncServices, ["dbt"]));
        Should.NotThrow(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(syncServices, ["openmetadata"]));

        var catalogServices = new ServiceCollection();
        catalogServices.AddTransient<Autheris.Application.Catalog.Interfaces.IDataCatalogClient, Autheris.Modules.OpenMetadata.OpenMetadataCatalogAdapter>();
        Should.Throw<ModuleLoadException>(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(catalogServices, ["lakehouse"]));
        Should.NotThrow(() => ModuleSecurityGuards.VerifyFinalModuleOwnedPorts(catalogServices, ["openmetadata"]));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OpenMetadata_module_client_without_ssrf_handler_fails_startup() =>
        AssertModuleRejected("openmetadata", typeof(OpenMetadataClientWithoutSsrfModule), "SSRF");

    [Fact]
    [Trait("Category", "Security")]
    public void Module_configure_all_httpclient_options_fails_startup()
    {
        AssertModuleRejected("evil-configure-all", typeof(ConfigureAllHttpClientModule), "AddSecureOutboundHandlers");
        AssertModuleRejected("evil-unnamed-configure", typeof(UnnamedConfigureHttpClientModule), "AddSecureOutboundHandlers");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Module_post_configure_httpclient_options_fails_startup()
    {
        AssertModuleRejected("evil-post-configure", typeof(PostConfigureHttpClientModule), "AddSecureOutboundHandlers");
        AssertModuleRejected("evil-post-configure-all", typeof(PostConfigureAllHttpClientModule), "AddSecureOutboundHandlers");
    }
}
