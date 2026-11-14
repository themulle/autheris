namespace Autheris.Tests.Integration;

using System.Collections.Generic;
using System.Text;
using Autheris.Application.Catalog.Semantic;
using Autheris.Extensions.Dbt;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// EXT-05: POST /api/extensions/dbt/sync hands the server-resolved request tenant and the canonical ClusterAdmin flag to the
/// ingestion service (the caller can no longer widen the tenant scope through the manifest).
/// </summary>
[Trait("Category", "Security")]
public sealed class DbtSyncEndpointCallerTests : IClassFixture<DbtSyncEndpointCallerTests.HostFixture>
{
    private readonly IDbtMetadataIngestionService _service;
    private readonly WebApplicationFactory<Program> _factory;

    public DbtSyncEndpointCallerTests(HostFixture fixture)
    {
        _factory = fixture;
        _service = fixture.Service;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        public IDbtMetadataIngestionService Service { get; } = Substitute.For<IDbtMetadataIngestionService>();

        public HostFixture()
        {
            Service.IngestManifestStreamAsync(Arg.Any<Stream>(), Arg.Any<DbtGovernanceImportCaller>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new DbtSyncResult(true, 0, 0, 0, []));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("DbtSyncEndpointCallerTests"),
            });
        }

        protected override void ConfigureHostServices(IServiceCollection services)
        {
            services.AddSingleton(Service);
        }
    }

    private async Task<DbtGovernanceImportCaller> PostAndCaptureCallerAsync(string sid, string roles, string tenant)
    {
        _service.ClearReceivedCalls();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);

        var response = await client.PostAsync("/api/extensions/dbt/sync", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.IsSuccessStatusCode.ShouldBeTrue();

        var call = _service.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDbtMetadataIngestionService.IngestManifestStreamAsync));
        return (DbtGovernanceImportCaller)call.GetArguments()[1]!;
    }

    [Fact]
    public async Task DbtSyncEndpoint_PassesCallerTenantAndClusterAdminFlag()
    {
        var tenantAdmin = await PostAndCaptureCallerAsync("S-1-5-21-GOV-1", "GovernanceAdmin", "tenant-a");

        tenantAdmin.Tenant.Value.ShouldBe("tenant-a");
        tenantAdmin.IsClusterAdmin.ShouldBeFalse();
        tenantAdmin.Actor.IsSync.ShouldBeFalse();
        tenantAdmin.Actor.Sid.Value.ShouldBe("S-1-5-21-GOV-1");

        _service.ClearReceivedCalls();

        var clusterAdmin = await PostAndCaptureCallerAsync("S-1-5-21-CLUSTER-1", "ClusterAdmin", "tenant-b");

        clusterAdmin.Tenant.Value.ShouldBe("tenant-b");
        clusterAdmin.IsClusterAdmin.ShouldBeTrue();
    }
}
