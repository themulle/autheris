namespace Autheris.Tests.Integration.Modules;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Application.Compliance;
using Autheris.Application.Lineage;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Host behaviour without the lineage module: OpenLineage sync route and config keys fail closed while GDPR disclosure remains in core.</summary>
[Trait("Category", "Security")]
public sealed class LineageModuleAbsentTests : IClassFixture<LineageModuleAbsentTests.AbsentHost>, IClassFixture<LineageModuleAbsentTests.ModuleHost>
{
    private readonly AbsentHost _absent;
    private readonly ModuleHost _withModule;

    public LineageModuleAbsentTests(AbsentHost absent, ModuleHost withModule)
    {
        _absent = absent;
        _withModule = withModule;
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-LINEAGE-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,ClusterAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant-Id", "tenant-lineage");
        return client;
    }

    [Fact]
    public async Task Lineage_routes_return_404_without_module()
    {
        using var client = Admin(_absent);
        foreach (var path in new[] { "/modules/lineage/openlineage/sync", "/api/lineage/openlineage/sync" })
        {
            using var response = await client.PostAsync(path, content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"POST {path} must be 404 without the lineage module.");
        }

        // Anonymous callers never see a 401 on a route that does not exist.
        using var anonymous = _absent.CreateClient();
        using var anonymousResponse = await anonymous.PostAsync("/modules/lineage/openlineage/sync", content: null);
        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // When the lineage module IS loaded, the module route enforces GovernanceAdmin + audits, while the legacy path stays 404.
        using var modAnon = _withModule.CreateClient();
        using var modDenied = await modAnon.PostAsync("/modules/lineage/openlineage/sync", content: null);
        modDenied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var modOwner = _withModule.CreateClient();
        modOwner.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-LINEAGE-OWNER");
        modOwner.DefaultRequestHeaders.Add("X-Test-Roles", "DataOwner");
        modOwner.DefaultRequestHeaders.Add("X-Test-Tenant-Id", "tenant-lineage");
        using var ownerDenied = await modOwner.PostAsync("/modules/lineage/openlineage/sync", content: null);
        ownerDenied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var modAdmin = Admin(_withModule);
        using var modSync = await modAdmin.PostAsync("/modules/lineage/openlineage/sync", content: null);
        modSync.StatusCode.ShouldBe(HttpStatusCode.BadGateway);

        using var legacy = await modAdmin.PostAsync("/api/lineage/openlineage/sync", content: null);
        legacy.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Lineage_config_section_fails_fast_without_module()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("LineageConfigAbsent"),
            ["Gateway:Lineage:OpenLineageEndpoint"] = "https://openlineage.example.com/api/v1/lineage",
        };
        using var host = new IntegrationHost(settings);

        var ex = Should.Throw<Exception>(() => host.CreateClient());
        ex.ToString().ShouldContain("Gateway:Lineage");
        ex.ToString().ShouldContain("module lineage is not installed");

        // The removed catalog key names its new path even when the module is absent.
        var removed = new Dictionary<string, string?>(settings)
        {
            ["Gateway:Catalog:" + "OpenLineageEndpoint"] = "https://openlineage.example.com/api/v1/lineage"
        };
        removed.Remove("Gateway:Lineage:OpenLineageEndpoint");
        using var removedHost = new IntegrationHost(removed);
        Should.Throw<Exception>(() => removedHost.CreateClient()).ToString().ShouldContain("Gateway:Lineage:OpenLineageEndpoint");
    }

    [Fact]
    public async Task Gdpr_report_route_still_served_without_lineage_module()
    {
        using var anonymous = _absent.CreateClient();
        using var unauthResponse = await anonymous.GetAsync("/api/governance/gdpr/export-pdf?domain=finance&schema=dbo&table=invoices");
        unauthResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var admin = _absent.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-PRIVACY-ADMIN");
        admin.DefaultRequestHeaders.Add("X-Test-Roles", "PrivacyAdmin,ClusterAdmin");
        admin.DefaultRequestHeaders.Add("X-Test-Tenant-Id", "tenant-lineage");
        using var response = await admin.GetAsync("/api/governance/gdpr/export-pdf?domain=finance&schema=dbo&table=invoices");
        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);

        using var scope = _absent.Services.CreateScope();
        scope.ServiceProvider.GetService<IGdprDisclosureReportService>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<IGdprAuditReportExporter>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<ILineageImpactAnalyzerService>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<ILineageGraphStore>().ShouldNotBeNull();
        scope.ServiceProvider.GetService<IOpenLineageClient>().ShouldBeNull();

        using var moduleScope = _withModule.Services.CreateScope();
        var lineageDescriptor = moduleScope.ServiceProvider
            .GetRequiredService<ModuleLoadResult>()
            .Modules
            .First(m => string.Equals(m.Instance.Id, "lineage", StringComparison.OrdinalIgnoreCase));
        var moduleOpenLineageInterface = lineageDescriptor.Instance.GetType().Assembly
            .GetType("Autheris.Application.Lineage.IOpenLineageClient", throwOnError: true)!;
        moduleScope.ServiceProvider.GetService(moduleOpenLineageInterface).ShouldNotBeNull();
    }

    public sealed class AbsentHost : IntegrationHost<AbsentHost>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("LineageModuleAbsentTests"),
            });
        }
    }

    public sealed class ModuleHost : IntegrationHost<ModuleHost>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("LineageModuleLoadedTests"),
            };
            LineageWorkspaceTestSettings.Apply((k, v) => settings[k] = v);
            config.AddInMemoryCollection(settings);
        }
    }
}
