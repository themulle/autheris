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

/// <summary>Issue #427 (CAT427-SC-04): the refresh physical keys endpoint is admin only.</summary>
[Trait("Category", "Security")]
public sealed class PhysicalKeyRefreshEndpointAuthIntegrationTests : IClassFixture<PhysicalKeyRefreshEndpointAuthIntegrationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PhysicalKeyRefreshEndpointAuthIntegrationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("PhysicalKeyRefreshEndpointAuthIntegrationTests"),
            });
        }
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
