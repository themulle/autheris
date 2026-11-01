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

/// <summary>Issue #427 (CAT427-SC-04): the refresh physical keys endpoint is admin only.</summary>
[Trait("Category", "Security")]
public sealed class PhysicalKeyRefreshEndpointAuthIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PhysicalKeyRefreshEndpointAuthIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
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
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-REFRESH-ADMIN");
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
            client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        }

        return client;
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasources/default/refresh-physical-keys")]
    [InlineData("/api/governance/datasources/default/refresh-physical-keys")]
    public async Task Anonymous_Returns401(string url)
    {
        var response = await CreateClient(null).PostAsJsonAsync(url, new { dryRun = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasources/default/refresh-physical-keys")]
    [InlineData("/api/governance/datasources/default/refresh-physical-keys")]
    public async Task NonAdmin_Returns403(string url)
    {
        var response = await CreateClient("DataConsumer").PostAsJsonAsync(url, new { dryRun = true });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
