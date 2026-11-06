namespace Autheris.Tests.Integration.Removal;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RemovedEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RemovedEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
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
    [InlineData("POST", "/api/v1/flight/sql/info")]
    [InlineData("GET", "/api/v1/flight/sql/tables")]
    [InlineData("POST", "/api/v1/flight/sql/stream")]
    [InlineData("GET", "/getting-started")]
    [InlineData("GET", "/portal/2fa/enroll")]
    [InlineData("POST", "/portal/2fa/enroll")]
    [InlineData("POST", "/portal/2fa/enroll/verify")]
    [InlineData("GET", "/portal/approvals")]
    [InlineData("POST", "/portal/approvals/t-123/step-up")]
    public async Task Removed_routes_return_404_for_admin_and_anonymous(string method, string route)
    {
        using var admin = CreateAdminClient();
        using var anon = CreateAnonymousClient();

        var reqAdmin = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAdmin.Content = JsonContent.Create(new { query = "SELECT 1" });
        }
        var resAdmin = await admin.SendAsync(reqAdmin);
        resAdmin.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Admin calling {method} {route} should 404");

        var reqAnon = new HttpRequestMessage(new HttpMethod(method), route);
        if (method == "POST")
        {
            reqAnon.Content = JsonContent.Create(new { query = "SELECT 1" });
        }
        var resAnon = await anon.SendAsync(reqAnon);
        resAnon.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"Anonymous calling {method} {route} should 404");
    }

    [Fact]
    public async Task Root_returns_no_dev_portal_html()
    {
        using var anon = CreateAnonymousClient();
        var res = await anon.GetAsync("/");
        if (res.IsSuccessStatusCode)
        {
            var content = await res.Content.ReadAsStringAsync();
            content.ShouldNotContain("DevPortal");
            content.ShouldNotContain("Autheris DevPortal");
        }
        else
        {
            res.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void EndpointDataSource_DoesNotContain_RemovedRoutes()
    {
        using var client = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToList();

        var removedPatterns = new[]
        {
            "/api/v1/flight/sql",
            "/getting-started",
            "/portal/2fa",
            "/portal/approvals"
        };

        foreach (var pattern in removedPatterns)
        {
            var match = endpoints.Where(e => (e.RoutePattern.RawText ?? "").Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
            match.ShouldBeEmpty($"Found endpoints matching removed pattern '{pattern}'");
        }
    }
}
