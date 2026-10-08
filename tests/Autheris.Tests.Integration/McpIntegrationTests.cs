namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public class McpIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public McpIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task McpStreamableHttpFlow_ShouldWorkEndToEnd()
    {
        await using var client = await McpTestClient.ConnectAsync(_factory.CreateClient());

        client.ServerInfo.Name.ShouldBe("Autheris.McpServer");

        var (_, text) = await McpTestClient.CallAsync(client, "query_customers", new { });

        // SEC M-17: Der MCP-Executor liefert keine erfundenen Beispieldaten mehr (frueher "Erika Mustermann"),
        // sondern echte Daten oder ein strukturiertes Fehler-Ergebnis.
        text.ShouldNotBeNullOrWhiteSpace();
        text.ShouldNotContain("erika.mustermann@acme-corp.com");
        text.ShouldNotContain("Erika Mustermann");
        text.ShouldNotContain("Diabetes Type 2");
    }

    [Fact]
    public async Task McpSimulateQueryAndResourcesFlow_ShouldWorkEndToEnd()
    {
        await using var client = await McpTestClient.ConnectAsync(_factory.CreateClient());

        // resources/list and resources/read
        var resources = await client.ListResourcesAsync();
        resources.ShouldNotBeEmpty();
        var read = await client.ReadResourceAsync(resources[0].Uri);
        read.Contents.ShouldNotBeEmpty();

        // simulate_query
        var (_, simulation) = await McpTestClient.CallAsync(client, "simulate_query",
            new { query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 15) { id name } }" });
        simulation.ShouldContain("isAllowed");
        simulation.ShouldContain("estimatedRowCount");

        // query_customers returns a _provenance footnote, or a structured error without a data source (SEC M-17), never mock data.
        var (_, customers) = await McpTestClient.CallAsync(client, "query_customers", new { });
        (customers.Contains("_provenance", StringComparison.Ordinal) ||
         customers.Contains("EXECUTION_FAILED", StringComparison.Ordinal) ||
         customers.Contains("FORBIDDEN", StringComparison.Ordinal)).ShouldBeTrue(customers);
        customers.ShouldNotContain("Erika Mustermann");
    }

    [Fact]
    public async Task McpSimulateQueryTool_ExceedingLimit_ReturnsBlockedResult()
    {
        await using var client = await McpTestClient.ConnectAsync(_factory.CreateClient());

        // first: 50000 exceeds the default limit of 10000
        var (_, text) = await McpTestClient.CallAsync(client, "simulate_query",
            new { query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 50000) { id name } }" });

        text.ShouldContain("\"isAllowed\":false");
        text.ShouldContain("Hard-Safety-Limit");
    }

    [Fact]
    public async Task H02_McpEndpoints_WhenOpenSchemaEnabled_StillRequireAuthentication()
    {
        // SEC H-02: OpenSchema only opens documentation/catalog routes. MCP (incl. tools/call) stays behind
        // authentication unless the production-blocked danger_bypass_mcp_auth switch is set.
        using var openFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Insecure:danger_bypass_mcp_auth", "false");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "false");
            builder.UseSetting("Gateway:OpenSchema", "true");
        });

        var client = openFactory.CreateClient();

        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(initPayload, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var postResp = await client.SendAsync(request);
        postResp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task M09_McpStreamableHttp_OversizedBody_IsRejectedWith413()
    {
        var client = _factory.CreateClient();

        var hugeParam = new string('a', 1024 * 1024 + 16);
        var payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + hugeParam + "\"}}";
        var response = await client.PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }
}
