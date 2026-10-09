namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task ProtectedResourceMetadata_RootPath_ServesMetadataOrRedirectsAnonymously()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        // Must NOT return 401 Unauthorized for anonymous clients
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        (response.StatusCode == HttpStatusCode.OK ||
         response.StatusCode == HttpStatusCode.Redirect ||
         response.StatusCode == HttpStatusCode.TemporaryRedirect).ShouldBeTrue();
    }

    [Fact]
    public async Task ProtectedResourceMetadata_AnonymousInProduction_NamesTheAuthorizationServer_AndOnlyAllowlistedFields()
    {
        var tempDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gov-test-{Guid.NewGuid():N}.db");
        var tempAnchor = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}.txt");
        await System.IO.File.WriteAllTextAsync(tempAnchor, "anchor-token");
        try
        {
            var prodFactory = _factory.WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={tempDb}");
                b.UseSetting("Gateway:GovernanceDb:ChainAnchorPath", tempAnchor);
                b.UseSetting("Gateway:Audit:ChainAnchorPath", tempAnchor);
                b.UseSetting("Gateway:GovernanceDb:AuditHmacKeyVaultRef", "audit-hmac-key");
                b.UseSetting("Gateway:GovernanceDb:AuditHmacKey", "0123456789012345678901234567890123456789");
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "false");
                b.UseSetting("Gateway:Mcp:AllowAnonymousDiscovery", "false");
                b.ConfigureServices(services =>
                {
                    services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new ConnectionItemsStartupFilter());
                });
            });

            var client = prodFactory.CreateClient();
            var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            root.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).ShouldContain(Authority);
            root.GetProperty("resource").GetString()!.ShouldEndWith("/mcp");

            // Allowlist assertion: only public fields permitted
            var allowedFields = new HashSet<string>(["resource", "authorization_servers", "scopes_supported", "bearer_methods_supported", "resource_name"]);
            foreach (var prop in root.EnumerateObject())
            {
                allowedFields.ShouldContain(prop.Name);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempDb))
            {
                try { System.IO.File.Delete(tempDb); } catch { }
            }
            if (System.IO.File.Exists(tempAnchor))
            {
                try { System.IO.File.Delete(tempAnchor); } catch { }
            }
        }
    }

    [Fact]
    public async Task AuthorizationServerMetadata_Returns404NotFound_VariantA()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-authorization-server");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OpenIdConfiguration_Returns404NotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/openid-configuration");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BatchRequest_IsRejectedWith400_AndJsonRpcError32600()
    {
        var client = AuthenticatedHttpClient();
        var payload = """[{"jsonrpc":"2.0","id":1,"method":"tools/list"},{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sample_rows","arguments":{}}}]""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("JSON-RPC batching is not supported");
    }

    [Fact]
    public async Task EmptyBatchRequest_IsRejectedWith400_AndJsonRpcError32600()
    {
        var client = AuthenticatedHttpClient();
        var payload = "[]";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32600);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("JSON-RPC batching is not supported");
    }

    [Fact]
    public async Task McpDeveloperCors_InDevelopmentWithFlag_HandlesPreflightOptions()
    {
        var corsFactory = _factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Gateway:Mcp:EnableDeveloperCors", "true");
            b.UseSetting("Gateway:Mcp:DeveloperCorsOrigins:0", "https://vscode.dev");
        });

        var client = corsFactory.CreateClient();

        // 1. Valid localhost preflight
        var request = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        request.Headers.Add("Origin", "http://localhost:6274");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Mcp-Session-Id, MCP-Protocol-Version");

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain("http://localhost:6274");
        var allowHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        allowHeaders.ShouldContain("Mcp-Session-Id");
        allowHeaders.ShouldContain("MCP-Protocol-Version");
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();

        // 2. Extra origin (vscode.dev) preflight
        var vsCodeRequest = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        vsCodeRequest.Headers.Add("Origin", "https://vscode.dev");
        vsCodeRequest.Headers.Add("Access-Control-Request-Method", "POST");

        var vsCodeResponse = await client.SendAsync(vsCodeRequest);
        vsCodeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        vsCodeResponse.Headers.GetValues("Access-Control-Allow-Origin").ShouldContain("https://vscode.dev");

        // 3. Evil origin preflight (localhost.evil.com) -> must not be allowed
        var evilRequest = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        evilRequest.Headers.Add("Origin", "http://localhost.evil.com");
        evilRequest.Headers.Add("Access-Control-Request-Method", "POST");

        var evilResponse = await client.SendAsync(evilRequest);
        evilResponse.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();

        // 4. Preflight on /api/v1/olap/query -> must not be allowed (policy scoped to /mcp)
        var olapRequest = new HttpRequestMessage(HttpMethod.Options, "/api/v1/olap/query");
        olapRequest.Headers.Add("Origin", "http://localhost:6274");
        olapRequest.Headers.Add("Access-Control-Request-Method", "POST");

        var olapResponse = await client.SendAsync(olapRequest);
        olapResponse.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    private sealed class ConnectionItemsStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>() == null)
                    {
                        context.Features.Set<Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature>(new TestConnectionItemsFeature());
                    }
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }

    private sealed class TestConnectionItemsFeature : Microsoft.AspNetCore.Connections.Features.IConnectionItemsFeature
    {
        public System.Collections.Generic.IDictionary<object, object?> Items { get; set; } = new SafeItemsDictionary();

        private sealed class SafeItemsDictionary : System.Collections.Generic.IDictionary<object, object?>
        {
            private readonly System.Collections.Generic.Dictionary<object, object?> _dict = new();

            public object? this[object key]
            {
                get => _dict.TryGetValue(key, out var val) ? val : null;
                set => _dict[key] = value;
            }

            public System.Collections.Generic.ICollection<object> Keys => _dict.Keys;
            public System.Collections.Generic.ICollection<object?> Values => _dict.Values;
            public int Count => _dict.Count;
            public bool IsReadOnly => false;
            public void Add(object key, object? value) => _dict[key] = value;
            public void Add(System.Collections.Generic.KeyValuePair<object, object?> item) => _dict.Add(item.Key, item.Value);
            public void Clear() => _dict.Clear();
            public bool Contains(System.Collections.Generic.KeyValuePair<object, object?> item) => ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<object, object?>>)_dict).Contains(item);
            public bool ContainsKey(object key) => _dict.ContainsKey(key);
            public void CopyTo(System.Collections.Generic.KeyValuePair<object, object?>[] array, int arrayIndex) => ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<object, object?>>)_dict).CopyTo(array, arrayIndex);
            public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<object, object?>> GetEnumerator() => _dict.GetEnumerator();
            public bool Remove(object key) => _dict.Remove(key);
            public bool Remove(System.Collections.Generic.KeyValuePair<object, object?> item) => ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<object, object?>>)_dict).Remove(item);
            public bool TryGetValue(object key, out object? value) => _dict.TryGetValue(key, out value);
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _dict.GetEnumerator();
        }
    }
}
