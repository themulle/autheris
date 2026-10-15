namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SEC-94-05): the keys endpoint is admin only, over real requests on both routes.</summary>
public sealed class CatalogKeysEndpointAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogKeysEndpointAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private HttpClient CreateClient(string? roles)
    {
        var client = _factory.CreateClient();
        if (roles != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-KEYS");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        }

        return client;
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasets/sales.dbo.orders/keys")]
    [InlineData("/api/catalog/datasets/sales.dbo.orders/keys")]
    public async Task Anonymous_Returns401(string url)
    {
        var response = await CreateClient(null).PutAsJsonAsync(url, new { primaryKey = new[] { "id" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasets/sales.dbo.orders/keys")]
    [InlineData("/api/catalog/datasets/sales.dbo.orders/keys")]
    public async Task NonAdmin_Returns403(string url)
    {
        var response = await CreateClient("DataConsumer").PutAsJsonAsync(url, new { primaryKey = new[] { "id" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_UnknownDataset_Returns404()
    {
        var response = await CreateClient("GovernanceAdmin").PutAsJsonAsync("/api/v1/catalog/datasets/sales.dbo.nothere/keys", new { primaryKey = new[] { "id" } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
