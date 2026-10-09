using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>
/// GAP-A: Verifies the endpoint metadata of the fully wired gateway.
/// GAP01: With the authenticated-user FallbackPolicy (SEC M-03) every route that was intentionally anonymous before
/// the remediation (health probes, dev portal, webhooks with their own signature auth, self-checking docs routes)
/// must carry an explicit AllowAnonymous; protected routes must not.
/// GAP02: Routes that legitimately accept bodies above the global Kestrel limit (2 MB) carry a RequestSizeLimit.
/// </summary>
public class IntegrationGapAEndpointMetadataTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const long Mb = 1024 * 1024;
    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationGapAEndpointMetadataTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
        });
    }

    private List<RouteEndpoint> GetRouteEndpoints()
    {
        // Creating a client starts the host so that all endpoint data sources are registered.
        using var client = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        return dataSource.Endpoints.OfType<RouteEndpoint>().ToList();
    }

    private static RouteEndpoint FindEndpoint(List<RouteEndpoint> endpoints, string method, string rawPattern)
    {
        var match = endpoints.FirstOrDefault(e =>
            string.Equals(e.RoutePattern.RawText, rawPattern, StringComparison.OrdinalIgnoreCase) &&
            // Endpoints mapped via Map(...) (e.g. /metrics) carry no HttpMethodMetadata and match every method.
            (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase) ?? true));
        match.ShouldNotBeNull($"Endpoint {method} {rawPattern} is not mapped.");
        return match!;
    }

    [Theory]
    [InlineData("GET", "/health/live")]
    [InlineData("GET", "/health/ready")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/getting-started")]
    [InlineData("GET", "/odata/v4/$swagger")]
    [InlineData("GET", "/docs")]
    [InlineData("POST", "/api/webhooks/openmetadata")]
    [InlineData("POST", "/api/webhooks/itsm/status-change")]
    [InlineData("POST", "/api/webhooks/servicenow")]
    [InlineData("POST", "/api/webhooks/jira")]
    [InlineData("POST", "/api/webhooks/catalog")]
    [InlineData("POST", "/api/v1/governance/catalog/webhook/{provider}")]
    [InlineData("POST", "/api/extensions/dbt/webhooks/dbt-cloud")]
    public void GAP01_PreviouslyAnonymousRoutes_AllowAnonymous(string method, string rawPattern)
    {
        var endpoint = FindEndpoint(GetRouteEndpoints(), method, rawPattern);

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull(
            $"{method} {rawPattern} must opt out of the authenticated-user FallbackPolicy explicitly.");
    }

    [Theory]
    [InlineData("GET", "/metrics")]
    [InlineData("POST", "/api/sql")]
    [InlineData("POST", "/api/v1/sql")]
    [InlineData("GET", "/api/auth/login")]
    [InlineData("GET", "/odata/v4/$openapi")]
    [InlineData("POST", "/api/extensions/dbt/sync")]
    public void GAP01_ProtectedRoutes_DoNotAllowAnonymous(string method, string rawPattern)
    {
        var endpoint = FindEndpoint(GetRouteEndpoints(), method, rawPattern);

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GAP01_AnonymousProbeAndDocsCalls_AreNotRejectedWith401()
    {
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/health/ready", "/", "/getting-started", "/odata/v4/$swagger", "/docs" })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"GET {path}");
        }
    }

    [Fact]
    public async Task GAP01_AnonymousCallToProtectedRoute_IsRejectedWith401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/governance/system/metrics");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/extensions/dbt/sync", 100 * Mb)]
    [InlineData("/api/extensions/dbt/validate-contract", 100 * Mb)]
    [InlineData("/api/extensions/dbt/run-results", 50 * Mb)]
    [InlineData("/api/extensions/dbt/webhooks/dbt-cloud", 10 * Mb)]
    [InlineData("/api/webhooks/catalog", 10 * Mb)]
    [InlineData("/api/v1/governance/catalog/webhook/{provider}", 10 * Mb)]
    [InlineData("/api/v1/cdc/events", 10 * Mb)]
    [InlineData("/api/schema-registry/publish", 10 * Mb)]
    [InlineData("/api/schema-registry/check", 10 * Mb)]
    [InlineData("/api/governance/catalog/ingest-openapi", 20 * Mb)]
    public void GAP02_LargeBodyRoutes_DeclareRequestSizeLimitAboveGlobalKestrelLimit(string rawPattern, long expectedBytes)
    {
        var endpoint = FindEndpoint(GetRouteEndpoints(), "POST", rawPattern);

        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();
        limit.ShouldNotBeNull($"POST {rawPattern} needs a RequestSizeLimit exception to the global 2 MB Kestrel limit.");
        limit!.MaxRequestBodySize.ShouldBe(expectedBytes);
    }
}
