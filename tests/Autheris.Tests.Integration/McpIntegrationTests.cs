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
    public async Task McpSseAndMessageFlow_ShouldWorkEndToEnd()
    {
        var client = _factory.CreateClient();

        // 1. Establish SSE Connection (GET /mcp/sse)
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        sseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        sseResponse.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

        using var stream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Read initial endpoint event
        var line1 = await reader.ReadLineAsync(); // "event: endpoint"
        var line2 = await reader.ReadLineAsync(); // "data: /mcp/message?sessionId=..."
        await reader.ReadLineAsync(); // empty line

        line1.ShouldBe("event: endpoint");
        line2.ShouldNotBeNull();
        line2.ShouldStartWith("data: /mcp/message?sessionId=");

        var messageUri = line2.Replace("data: ", "").Trim();
        var sessionId = messageUri.Substring(messageUri.IndexOf("sessionId=", StringComparison.Ordinal) + 10);
        sessionId.ShouldNotBeNullOrWhiteSpace();

        // 2. Send JSON-RPC 'initialize' to POST /mcp/message
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResp = await client.PostAsync(messageUri, new StringContent(initPayload, Encoding.UTF8, "application/json"));
        initResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var initJson = await initResp.Content.ReadAsStringAsync();
        using var initDoc = JsonDocument.Parse(initJson);
        initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("Autheris.McpServer");

        // 3. Send JSON-RPC 'tools/call' for 'query_customers'
        var callPayload = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"query_customers","arguments":{}}}""";
        var callResp = await client.PostAsync(messageUri, new StringContent(callPayload, Encoding.UTF8, "application/json"));
        callResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var callJson = await callResp.Content.ReadAsStringAsync();
        using var callDoc = JsonDocument.Parse(callJson);
        var content = callDoc.RootElement.GetProperty("result").GetProperty("content");
        content.GetArrayLength().ShouldBe(1);
        var text = content[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();

        // SEC M-17: Der MCP-Executor liefert keine erfundenen Beispieldaten mehr (frueher "Erika Mustermann"),
        // sondern echte Daten oder ein strukturiertes Fehler-Ergebnis.
        text.ShouldNotContain("erika.mustermann@acme-corp.com");
        text.ShouldNotContain("Erika Mustermann");
        text.ShouldNotContain("Diabetes Type 2");

        // 4. Teardown session
        var deleteResp = await client.DeleteAsync($"/mcp/session/{sessionId}");
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task McpSimulateQueryAndResourcesFlow_ShouldWorkEndToEnd()
    {
        var client = _factory.CreateClient();

        // 1. Establish SSE Connection
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        sseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var stream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        await reader.ReadLineAsync(); // "event: endpoint"
        var line2 = await reader.ReadLineAsync(); // "data: /mcp/message?sessionId=..."
        await reader.ReadLineAsync(); // empty line

        var messageUri = line2!.Replace("data: ", "").Trim();
        var sessionId = messageUri.Substring(messageUri.IndexOf("sessionId=", StringComparison.Ordinal) + 10);

        // 2. Initialize
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResp = await client.PostAsync(messageUri, new StringContent(initPayload, Encoding.UTF8, "application/json"));
        initResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Test 'resources/list'
        var resListPayload = """{"jsonrpc":"2.0","id":2,"method":"resources/list","params":{}}""";
        var resListResp = await client.PostAsync(messageUri, new StringContent(resListPayload, Encoding.UTF8, "application/json"));
        resListResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resListJson = await resListResp.Content.ReadAsStringAsync();
        using var resDoc = JsonDocument.Parse(resListJson);
        var resources = resDoc.RootElement.GetProperty("result").GetProperty("resources");
        resources.EnumerateArray().Any().ShouldBeTrue();

        var firstUri = resources[0].GetProperty("uri").GetString();
        firstUri.ShouldNotBeNull();

        // 4. Test 'resources/read'
        var resReadPayload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "resources/read",
            @params = new { uri = firstUri }
        });
        var resReadResp = await client.PostAsync(messageUri, new StringContent(resReadPayload, Encoding.UTF8, "application/json"));
        resReadResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resReadJson = await resReadResp.Content.ReadAsStringAsync();
        resReadJson.ShouldContain("contents");

        // 5. Test 'simulate_query' tool call
        var simCallPayload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "simulate_query",
                arguments = new { query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 15) { id name } }" }
            }
        });
        var simCallResp = await client.PostAsync(messageUri, new StringContent(simCallPayload, Encoding.UTF8, "application/json"));
        simCallResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var simCallJson = await simCallResp.Content.ReadAsStringAsync();
        simCallJson.ShouldContain("isAllowed");
        simCallJson.ShouldContain("estimatedRowCount");

        // 6. Test 'query_customers' returns _provenance footnote
        var custPayload = """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"query_customers","arguments":{}}}""";
        var custResp = await client.PostAsync(messageUri, new StringContent(custPayload, Encoding.UTF8, "application/json"));
        custResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var custJson = await custResp.Content.ReadAsStringAsync();
        // SEC M-17: Ohne echte Datenquelle liefert der Executor ein strukturiertes Fehler-Ergebnis (ohne Provenance) statt Mock-Daten.
        (custJson.Contains("_provenance", StringComparison.Ordinal) ||
         custJson.Contains("EXECUTION_FAILED", StringComparison.Ordinal) ||
         custJson.Contains("FORBIDDEN", StringComparison.Ordinal)).ShouldBeTrue();
        custJson.ShouldNotContain("Erika Mustermann");

        // Teardown
        var deleteResp = await client.DeleteAsync($"/mcp/session/{sessionId}");
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task McpSimulateQueryTool_ExceedingLimit_ReturnsBlockedResult()
    {
        var client = _factory.CreateClient();

        // 1. Establish SSE Connection
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        sseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var stream = await sseResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        await reader.ReadLineAsync(); // "event: endpoint"
        var line2 = await reader.ReadLineAsync(); // "data: /mcp/message?sessionId=..."
        await reader.ReadLineAsync(); // empty line

        var messageUri = line2!.Replace("data: ", "").Trim();
        var sessionId = messageUri.Substring(messageUri.IndexOf("sessionId=", StringComparison.Ordinal) + 10);

        // 2. Initialize
        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var initResp = await client.PostAsync(messageUri, new StringContent(initPayload, Encoding.UTF8, "application/json"));
        initResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Test 'simulate_query' tool call with first: 50000 (exceeds default 10000 limit)
        var simCallPayload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "simulate_query",
                arguments = new { query = @"query { table(domain: ""finance"", name: ""finance_table_1"", first: 50000) { id name } }" }
            }
        });
        var simCallResp = await client.PostAsync(messageUri, new StringContent(simCallPayload, Encoding.UTF8, "application/json"));
        simCallResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var simCallJson = await simCallResp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(simCallJson);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        var text = content[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text.ShouldContain("\"isAllowed\":false");
        text.ShouldContain("Hard-Safety-Limit");

        // Teardown
        var deleteResp = await client.DeleteAsync($"/mcp/session/{sessionId}");
        deleteResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
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

        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp/sse");
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead);
        sseResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var initPayload = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var postResp = await client.PostAsync("/mcp", new StringContent(initPayload, Encoding.UTF8, "application/json"));
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
