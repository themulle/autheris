namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

/// <summary>
/// The MCP endpoint speaks the current protocol through the official SDK: a standard MCP client connects, receives the
/// server instructions, lists tools and resource templates; unauthenticated clients learn from the OAuth protected
/// resource metadata where to get a token.
/// </summary>
public sealed class McpSdkTransportTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Authority = "https://login.example.test/adfs";
    private readonly WebApplicationFactory<Program> _factory;

    public McpSdkTransportTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:Authentication:Adfs:Enabled", "true");
            b.UseSetting("Gateway:Authentication:Adfs:Authority", Authority);
            b.UseSetting("Gateway:Authentication:Adfs:Audience", "api://autheris");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-McpSdk-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
        });
    }

    private HttpClient AuthenticatedHttpClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-MCP-SDK-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sdk");
        return client;
    }

    private async Task<McpClient> ConnectAsync()
    {
        var http = AuthenticatedHttpClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task StandardClient_Connects_AndIsToldToQueryWithGraphQl()
    {
        await using var client = await ConnectAsync();

        client.ServerInstructions.ShouldNotBeNull();
        client.ServerInstructions.ShouldContain("query_graphql");
        client.ServerInfo.Name.ShouldBe("Autheris.McpServer");
        // The SDK serves the current protocol revisions; the old hand-written server only spoke 2024-11-05.
        string.CompareOrdinal(client.NegotiatedProtocolVersion, "2025-11-25").ShouldBeGreaterThanOrEqualTo(0, client.NegotiatedProtocolVersion);
    }

    [Fact]
    public async Task StandardClient_ListsTheDatasetTools_WithValidSchemas()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        names.ShouldContain("query_graphql");
        names.ShouldContain("list_datasets");
        names.ShouldContain("describe_dataset");
        names.ShouldContain("sample_rows");
        tools.Single(t => t.Name == "describe_dataset").JsonSchema.GetProperty("required")[0].GetString().ShouldBe("dataset");
    }

    [Fact]
    public async Task StandardClient_CallsATool()
    {
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("list_datasets", new System.Collections.Generic.Dictionary<string, object?>());

        result.IsError.ShouldNotBe(true);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("endpoints")[0].GetProperty("protocol").GetString().ShouldBe("GraphQL");
    }

    [Fact]
    public async Task StandardClient_ListsTheDatasetResourceTemplate()
    {
        await using var client = await ConnectAsync();

        var templates = await client.ListResourceTemplatesAsync();

        templates.ShouldContain(t => t.UriTemplate == "autheris://datasets/{dataset}");
    }

    [Fact]
    public async Task UnauthenticatedRequest_Is401_WithTheResourceMetadataLink()
    {
        var client = _factory.CreateClient();
        var body = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = body };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("resource_metadata=");
    }

    [Fact]
    public async Task ProtectedResourceMetadata_NamesTheAuthorizationServer()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).ShouldContain(Authority);
        doc.RootElement.GetProperty("resource").GetString()!.ShouldEndWith("/mcp");
    }

    [Fact]
    public async Task OversizedMessage_IsRejectedWith413()
    {
        var client = AuthenticatedHttpClient();
        var payload = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"pad":""" + "\"" + new string('x', 1_100_000) + "\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
