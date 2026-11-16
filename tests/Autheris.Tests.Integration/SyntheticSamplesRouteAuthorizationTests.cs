namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Tests.Common.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// MCP-01: the GET synthetic-samples route returns predefined sample rows of a table, so it is a GovernanceAdmin route like the PUT.
/// </summary>
[Trait("Category", "Security")]
public sealed class SyntheticSamplesRouteAuthorizationTests : IClassFixture<SyntheticSamplesRouteAuthorizationTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SyntheticSamplesRouteAuthorizationTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("SyntheticSamplesRouteAuthorizationTests"));
        }
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasets/{id}/discovery-artifacts", "PUT")]
    [InlineData("/api/v1/catalog/datasets/{id}/discovery-artifacts", "DELETE")]
    public void DiscoveryArtifactMutation_RequiresAuthentication_AndTheServiceEnforcesUploaderOrOwner(string pattern, string method)
    {
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

        var matches = endpoints
            .Where(e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.OrdinalIgnoreCase) &&
                        (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) ?? false))
            .ToList();

        matches.ShouldNotBeEmpty();
        // Dataset owners may upload too, so the route only requires an authenticated caller; the uploader-or-owner decision is made in the
        // service (see DiscoveryArtifactPut_NonUploader_IsForbidden).
        matches.ShouldAllBe(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any());
    }

    [Fact]
    public async Task DiscoveryArtifactPut_NonUploader_IsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "AiAgent");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");

        using var content = new StringContent("""{"schemaVersion":1,"samples":[],"statistics":{}}""", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/v1/catalog/datasets/sales.public.orders/discovery-artifacts", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/catalog/datasets/sales.public.orders/synthetic-samples")]
    [InlineData("/api/v1/catalog/datasets/sales.public.orders/synthetic-samples")]
    public async Task LegacySyntheticSamplesRoutes_AreRemoved(string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");

        var response = await client.GetAsync(route);

        // The route is gone: the dataset catch-all swallows the path and rejects it as an unknown or malformed dataset id.
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }
}
