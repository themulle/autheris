namespace Autheris.Tests.Integration.Removal;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class BorderlineRemovalTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BorderlineRemovalTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GraphQL:PersistedQueriesOnly", "true");
            builder.UseSetting("Gateway:GraphQL:AppScopedTrustedDocuments:Enabled", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        });
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "ClusterAdmin,GovernanceAdmin");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        return client;
    }

    private HttpClient CreateAnonymousClient()
    {
        return _factory.CreateClient();
    }

    [Theory]
    [InlineData("GET", "/api/v1/governance/dp/budget")]
    [InlineData("POST", "/api/v1/governance/dp/budget/reset")]
    [InlineData("POST", "/api/v1/governance/dp/perturb")]
    public async Task DifferentialPrivacy_routes_return_404(string method, string route)
    {
        using var admin = CreateAdminClient();
        using var anon = CreateAnonymousClient();

        var reqAdmin = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAdmin.Content = JsonContent.Create(new { epsilon = 0.5 });
        }
        var resAdmin = await admin.SendAsync(reqAdmin);
        resAdmin.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Admin calling {method} {route} should 404");

        var reqAnon = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAnon.Content = JsonContent.Create(new { epsilon = 0.5 });
        }
        var resAnon = await anon.SendAsync(reqAnon);
        resAnon.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Anonymous calling {method} {route} should 404");
    }

    [Fact]
    public void Cdn_has_no_routes_and_no_outbound_client()
    {
        var dataSource = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoints = dataSource.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();
        var cdnEndpoints = endpoints.Where(e => (e.RoutePattern.RawText ?? "").Contains("cdn", StringComparison.OrdinalIgnoreCase)).ToList();
        cdnEndpoints.ShouldBeEmpty();

        var clientFactoryOptions = _factory.Services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>();
        if (clientFactoryOptions != null)
        {
            clientFactoryOptions.Get("CloudflareCdnPurgeService").HttpClientActions.Count.ShouldBe(0);
            clientFactoryOptions.Get("FastlyCdnPurgeService").HttpClientActions.Count.ShouldBe(0);
        }
    }

    [Theory]
    [InlineData("POST", "/api/schema-registry/publish")]
    [InlineData("POST", "/api/schema-registry/check")]
    [InlineData("GET", "/api/schema-registry/services")]
    [InlineData("GET", "/api/schema-registry/accounts/latest")]
    [InlineData("GET", "/api/schema-registry/accounts/history")]
    public async Task SchemaRegistry_routes_return_404(string method, string route)
    {
        using var admin = CreateAdminClient();
        using var anon = CreateAnonymousClient();

        var reqAdmin = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAdmin.Content = JsonContent.Create(new { serviceName = "accounts", sdl = "type Query { a: Int }" });
        }
        var resAdmin = await admin.SendAsync(reqAdmin);
        resAdmin.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Admin calling {method} {route} should 404");

        var reqAnon = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAnon.Content = JsonContent.Create(new { serviceName = "accounts", sdl = "type Query { a: Int }" });
        }
        var resAnon = await anon.SendAsync(reqAnon);
        resAnon.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Anonymous calling {method} {route} should 404");
    }

    [Fact]
    public async Task TrustedDocument_admin_routes_still_require_admin()
    {
        using var anon = CreateAnonymousClient();
        var resAnon = await anon.GetAsync("/api/admin/trusted-documents/shop");
        resAnon.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
