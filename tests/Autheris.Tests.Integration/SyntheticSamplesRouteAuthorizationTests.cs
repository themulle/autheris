namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-01: the GET synthetic-samples route returns predefined sample rows of a table, so it is a GovernanceAdmin route like the PUT.
/// </summary>
[Trait("Category", "Security")]
public sealed class SyntheticSamplesRouteAuthorizationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SyntheticSamplesRouteAuthorizationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Theory]
    [InlineData("/api/catalog/datasets/{id}/synthetic-samples")]
    public void GetSyntheticSamples_RequiresGovernanceAdminPolicy(string pattern)
    {
        // Creating a client starts the host so that all endpoint data sources are registered.
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var matches = endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.OrdinalIgnoreCase) &&
                        (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET", StringComparer.OrdinalIgnoreCase) ?? false))
            .ToList();

        matches.ShouldNotBeEmpty();
        matches.ShouldAllBe(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == GatewayPolicies.GovernanceAdmin));
    }

    [Fact]
    public async Task GetSyntheticSamples_NonAdmin_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "AiAgent");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");

        var response = await client.GetAsync("/api/catalog/datasets/sales.public.orders/synthetic-samples");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
