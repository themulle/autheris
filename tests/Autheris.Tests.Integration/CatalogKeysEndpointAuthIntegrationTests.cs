namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>Issue #94 (SEC-94-05): the keys endpoint is admin only, over real requests on both routes.</summary>
public sealed class CatalogKeysEndpointAuthIntegrationTests : IClassFixture<CatalogKeysEndpointAuthIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogKeysEndpointAuthIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("CatalogKeysEndpointAuthIntegrationTests"),
            });
        }
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
