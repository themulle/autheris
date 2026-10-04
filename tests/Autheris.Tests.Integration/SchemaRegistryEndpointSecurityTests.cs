namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public class SchemaRegistryEndpointSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SchemaRegistryEndpointSecurityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task SchemaRegistry_Publish_Unauthenticated_ReturnsUnauthorized()
    {
        // Finding A2 Verification:
        // /api/schema-registry/publish must require authorization
        var client = _factory.CreateClient(); // No auth headers

        var payload = """
        {
            "serviceName": "accounts",
            "sdl": "type Query { ping: String }"
        }
        """;

        var response = await client.PostAsync("/api/schema-registry/publish", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SchemaRegistry_Check_Unauthenticated_ReturnsUnauthorized()
    {
        // Finding A2 Verification:
        // /api/schema-registry/check must require authorization
        var client = _factory.CreateClient();

        var payload = """
        {
            "serviceName": "accounts",
            "sdl": "type Query { ping: String }"
        }
        """;

        var response = await client.PostAsync("/api/schema-registry/check", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SchemaRegistry_GetServices_Unauthenticated_ReturnsUnauthorized()
    {
        // Finding A2 Verification:
        // /api/schema-registry/services must require authorization
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/schema-registry/services");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SchemaRegistry_Publish_ForceIfBreaking_ByNonAdmin_ReturnsForbidden()
    {
        // Finding A2 Verification:
        // ForceIfBreaking = true requires administrative privileges (e.g. GovernanceAdmin, SchemaAdmin, ClusterAdmin).
        // A regular reader or developer must be rejected with 403 Forbidden.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-NORMAL-DEV");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Developer,Reader");

        var payload = """
        {
            "serviceName": "accounts",
            "sdl": "type Query { ping: String }",
            "forceIfBreaking": true
        }
        """;

        var response = await client.PostAsync("/api/schema-registry/publish", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("administrative privileges");
    }

    [Fact]
    public async Task SchemaRegistry_Publish_ForceIfBreaking_BySchemaAdmin_DoesNotReturnForbidden()
    {
        // Finding A2 Verification:
        // ForceIfBreaking = true allowed for SchemaAdmin
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SCHEMA-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "SchemaAdmin");

        var payload = """
        {
            "serviceName": "accounts",
            "sdl": "type Query { ping: String }",
            "forceIfBreaking": true
        }
        """;

        var response = await client.PostAsync("/api/schema-registry/publish", new StringContent(payload, Encoding.UTF8, "application/json"));
        // Should not be 401 or 403 (can be 200 OK or 400 if validation fails, but authz passes)
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }
}
