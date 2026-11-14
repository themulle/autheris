namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// EP-11: Confirms that access policy REST routes are completely removed and return 404.
/// </summary>
[Trait("Category", "Security")]
public sealed class AccessPolicyApiRemovedTests : IClassFixture<AccessPolicyApiRemovedTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AccessPolicyApiRemovedTests(HostFixture factory)
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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("AccessPolicyApiRemovedTests"),
            });
        }
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        return client;
    }

    [Fact]
    public void EndpointDataSource_DoesNotContain_PolicyRoutes()
    {
        using var client = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var policyEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").Contains("governance/policies", StringComparison.OrdinalIgnoreCase))
            .ToList();

        policyEndpoints.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GET", "/api/v1/tenants/tenant-default/governance/policies")]
    [InlineData("POST", "/api/v1/tenants/tenant-default/governance/policies")]
    [InlineData("PUT", "/api/v1/tenants/tenant-default/governance/policies/sample-id")]
    [InlineData("DELETE", "/api/v1/tenants/tenant-default/governance/policies/sample-id")]
    public async Task PolicyRoutes_AreNotMapped(string method, string url)
    {
        var client = CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
