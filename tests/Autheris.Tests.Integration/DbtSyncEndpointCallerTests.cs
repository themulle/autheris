namespace Autheris.Tests.Integration;

using System.Text;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-05: POST /api/extensions/dbt/sync hands the server-resolved request tenant and the canonical ClusterAdmin flag to the
/// ingestion service (the caller can no longer widen the tenant scope through the manifest).
/// </summary>
[Trait("Category", "Security")]
public sealed class DbtSyncEndpointCallerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly IDbtMetadataIngestionService _service = Substitute.For<IDbtMetadataIngestionService>();
    private readonly WebApplicationFactory<Program> _factory;

    public DbtSyncEndpointCallerTests(WebApplicationFactory<Program> factory)
    {
        _service.IngestManifestStreamAsync(Arg.Any<Stream>(), Arg.Any<DbtGovernanceImportCaller>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DbtSyncResult(true, 0, 0, 0, []));

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_service);
            });
        });
    }

    private async Task<DbtGovernanceImportCaller> PostAndCaptureCallerAsync(string sid, string roles, string tenant)
    {
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
