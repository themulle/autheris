namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public sealed class LakehouseModuleAbsentSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LakehouseModuleAbsentSecurityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    [Trait("Category", "Security")]
    public async Task Iceberg_routes_404_when_absent()
    {
        await using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            GovernanceTestDatabase.Apply(builder, "LakehouseAbsent");
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", "tenant-1");
        });

        using var client = host.CreateClient();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/modules/lakehouse/iceberg/v1/tenant-1/namespaces"),
            (HttpMethod.Get, "/modules/lakehouse/iceberg/v1/tenant-1/namespaces/raw/tables"),
            (HttpMethod.Get, "/modules/lakehouse/iceberg/v1/tenant-1/namespaces/raw/tables/orders"),
            (HttpMethod.Post, "/modules/lakehouse/iceberg/v1/tenant-1/namespaces/raw/tables/orders/credentials")
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Test-User-Sid", "S-1-5-21-LAKEHOUSE-CONTRACT");
            request.Headers.Add("X-Test-Roles", "GovernanceAdmin");
            request.Headers.Add("X-Test-Tenant-Id", "tenant-1");

            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Expected 404 when Lakehouse module is not enabled for {method} {path}.");
        }
    }
}
