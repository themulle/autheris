namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// API-2: the JSON ext_authz endpoint decided with the identity of the calling mesh service (confused deputy)
/// and is no longer reachable.
/// </summary>
public sealed class EnvoyJsonModeRemovedIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EnvoyJsonModeRemovedIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task JsonCheckEndpoint_IsNotMapped()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-MESH-SERVICE");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.PostAsJsonAsync("/api/v1/envoy/authz", new { attributes = new { request = new { http = new { method = "GET", path = "/api/orders" } } } });

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }
}
