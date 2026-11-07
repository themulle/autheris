namespace Autheris.Tests.Integration;

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

/// <summary>SR-L-02: host filtering and the CSRF origin check run against the allow-list in the real pipeline (Development: loopback only).</summary>
public sealed class HostFilteringPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostFilteringPipelineTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("HostFilteringPipelineTests"));
        });
    }

    private static HttpRequestMessage GraphQlPost(string host, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/graphql")
        {
            Content = new StringContent("{\"query\":\"{ __typename }\"}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Host = host;
        request.Headers.Add("GraphQL-Preflight", "1");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return request;
    }

    [Fact]
    public async Task Request_WithUnlistedHost_Returns400()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        request.Headers.Host = "evil.example";

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HealthEndpoint_UnlistedHost_StillServed()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "10.1.2.3:5000";

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Csrf_OriginEqualsHostButHostNotAllowed_Rejected()
    {
        var client = _factory.CreateClient();
        using var request = GraphQlPost("evil.example", "http://evil.example");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Csrf_OriginInAllowList_Accepted()
    {
        var client = _factory.CreateClient();
        using var request = GraphQlPost("localhost", "http://localhost");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Csrf_ForeignOriginOnAllowedHost_Forbidden()
    {
        var client = _factory.CreateClient();
        using var request = GraphQlPost("localhost", "http://evil.example");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
