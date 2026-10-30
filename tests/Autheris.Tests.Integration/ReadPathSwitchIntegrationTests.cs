namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

/// <summary>Shared host set-up of the PLAN-SEC-115 integration tests (SQLite governance store, test auth, MCP on).</summary>
internal static class ReadPathHost
{
    public static WebApplicationFactory<Program> With(WebApplicationFactory<Program> factory, params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-rps-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

    public static HttpClient Authenticated(WebApplicationFactory<Program> factory)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-RPS-USER");
        c.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_rps");
        return c;
    }

    public static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        var http = Authenticated(factory);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    public static IReadOnlyList<(string Pattern, string HandlerType, string[] ServiceParams)> Routes(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e =>
            {
                var method = e.Metadata.GetMetadata<MethodInfo>();
                return (
                    e.RoutePattern.RawText ?? string.Empty,
                    method?.DeclaringType?.Name ?? string.Empty,
                    (method?.GetParameters().Select(p => p.ParameterType.Name).ToArray() ?? []));
            })
            .ToList();
}

/// <summary>PLAN-SEC-115 WP-4 with <c>Gateway:RestApi:DataEndpoint:Enabled=false</c>: the REST data family is not mapped (router 404).</summary>
public sealed class ReadPathRestSwitchOffIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] ClosedRequests =
    [
        "GET /api/v1/data/d/s/t",
        "GET /api/v1/data/d/s/t/1",
        "GET /api/v1/data/d.s.t",
        "GET /api/v1/data/d/s/t/change-token",
        "POST /api/v1/data/d/s/t/changes",
        "GET /api/v1/catalog/datasets/a.b.c/sample-rows"
    ];

    private readonly WebApplicationFactory<Program> _factory;

    public ReadPathRestSwitchOffIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = ReadPathHost.With(factory, ("Gateway:RestApi:DataEndpoint:Enabled", "false"))
            ;
    }

    private static string MaskRequestIds(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"(correlationId|traceId)\":\"[^\"]*\"", "\"$1\":\"<ID>\"");

    private static HttpRequestMessage Request(string spec)
    {
        var parts = spec.Split(' ', 2);
        var request = new HttpRequestMessage(new HttpMethod(parts[0]), parts[1]);
        if (parts[0] == "POST") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<(HttpStatusCode Status, string Body, string[] Headers)> Send(HttpClient client, string spec)
    {
        var response = await client.SendAsync(Request(spec));
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Contains("trace", StringComparison.OrdinalIgnoreCase) && !h.Key.Contains("correlation", StringComparison.OrdinalIgnoreCase) &&
                        !h.Key.Contains("lineage", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), headers);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_01_and_19_Closed_routes_are_404_with_empty_body_and_equal_an_unknown_path_for_both_caller_kinds()
    {
        var authenticated = ReadPathHost.Authenticated(_factory);
        var anonymous = _factory.CreateClient();

        foreach (var client in new[] { authenticated, anonymous })
        {
            var unknown = await Send(client, "GET /api/v1/data-nonexistent-xyz");
            // Anonymous callers get the fallback policy's 401 for any unmapped path (so closed and unknown cannot be told apart either).
            unknown.Status.ShouldBe(ReferenceEquals(client, authenticated) ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized);
            // The dataset catch-all route /api/v1/catalog/datasets/{*id} answers any deeper path: the closed sample-rows route must
            // look exactly like another unknown sub-path there.
            var unknownDataset = await Send(client, "GET /api/v1/catalog/datasets/a.b.c/zzz-nonexistent-xyz");
            foreach (var spec in ClosedRequests)
            {
                var closed = await Send(client, spec);
                var reference = spec.Contains("sample-rows", StringComparison.Ordinal) ? unknownDataset : unknown;
                closed.Status.ShouldBe(reference.Status, spec);
                if (ReferenceEquals(client, authenticated) && !spec.Contains("sample-rows", StringComparison.Ordinal)) closed.Body.ShouldBe(string.Empty, spec);
                // The catch-all answers both with the same governed problem; only the per-request ids differ, so they are masked.
                MaskRequestIds(closed.Body.Replace("sample-rows", "<X>", StringComparison.Ordinal)).ShouldBe(MaskRequestIds(reference.Body.Replace("zzz-nonexistent-xyz", "<X>", StringComparison.Ordinal)), spec);
                closed.Headers.ShouldBe(reference.Headers, spec);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_02_No_route_of_the_REST_data_family_or_its_handlers_remains()
    {
        var routes = ReadPathHost.Routes(_factory);

        routes.ShouldNotBeEmpty();
        routes.Where(r => r.Pattern.StartsWith("/api/v1/data", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
        routes.Where(r => r.Pattern.Contains("sample-rows", StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty();
        routes.Where(r => r.HandlerType is "DatasetDataEndpoints" or "ChangeDetectionEndpoints").ShouldBeEmpty();
        routes.Where(r => r.ServiceParams.Contains("IGovernedChangeDetectionService")).ShouldBeEmpty();
    }

    [Fact]
    public void Neighbouring_routes_are_still_mapped()
    {
        var patterns = ReadPathHost.Routes(_factory).Select(r => r.Pattern).ToList();

        patterns.ShouldContain(p => p.StartsWith("/api/v1/catalog/datasets", StringComparison.Ordinal));
        patterns.ShouldContain("/api/v1/analytics/aggregate");
        patterns.ShouldContain(p => p.StartsWith("/api/v1/sql", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_17_The_observer_counts_closed_requests_and_ignores_other_paths()
    {
        using var probe = new RestDataCounter();
        var client = ReadPathHost.Authenticated(_factory);

        await client.GetAsync("/api/v1/data/d/s/t");
        await client.GetAsync("/api/v1/database");
        await client.GetAsync("/api/v1/catalog/datasets");

        probe.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_18_The_snapshot_and_the_mapping_agree_and_do_not_follow_a_later_configuration_change()
    {
        var switches = _factory.Services.GetRequiredService<IReadPathSwitches>();
        switches.RestDataEndpointEnabled.ShouldBeFalse();

        var config = (IConfigurationRoot)_factory.Services.GetRequiredService<IConfiguration>();
        config["Gateway:RestApi:DataEndpoint:Enabled"] = "true";

        _factory.Services.GetRequiredService<IReadPathSwitches>().RestDataEndpointEnabled.ShouldBeFalse();
        (await ReadPathHost.Authenticated(_factory).GetAsync("/api/v1/data/d/s/t")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        ReferenceEquals(switches, _factory.Services.GetRequiredService<IReadPathSwitches>()).ShouldBeTrue();
    }
}

/// <summary>PLAN-SEC-115 WP-4 with defaults: nothing changes (AC 4).</summary>
public sealed class ReadPathRestSwitchDefaultIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadPathRestSwitchDefaultIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = ReadPathHost.With(factory);
    }

    [Fact]
    public void The_REST_data_family_is_mapped_by_default()
    {
        var routes = ReadPathHost.Routes(_factory);
        var patterns = routes.Select(r => r.Pattern).ToList();

        patterns.ShouldContain("/api/v1/data/{domain}/{schema}/{table}");
        patterns.ShouldContain("/api/v1/data/{domain}/{schema}/{table}/{id}");
        patterns.ShouldContain("/api/v1/data/{datasetId}");
        patterns.ShouldContain("/api/v1/data/{domain}/{schema}/{table}/change-token");
        patterns.ShouldContain("/api/v1/data/{domain}/{schema}/{table}/changes");
        patterns.ShouldContain("/api/v1/catalog/datasets/{dataset}/sample-rows");
        patterns.ShouldContain("/api/v1/analytics/aggregate");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_01_The_sample_rows_route_is_mapped_by_default_and_requires_authorization()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/catalog/datasets/a.b.c/sample-rows");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_17_The_observer_is_not_registered_when_the_switch_is_on()
    {
        using var probe = new RestDataCounter();

        await _factory.CreateClient().GetAsync("/api/v1/data/d/s/t");

        probe.Count.ShouldBe(0);
    }
}

/// <summary>PLAN-SEC-115 test 23 and ST-115-10/11 over the real SDK transport: Query and Api groups off, REST data off.</summary>
public sealed class ReadPathMcpSwitchIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] QueryTools = ["query_sql", "query_graphql", "sample_rows", "query_dataset", "query_metrics", "simulate_query"];
    private static readonly string[] ApiTools = ["invoke_api", "describe_api"];

    private readonly WebApplicationFactory<Program> _factory;

    public ReadPathMcpSwitchIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = ReadPathHost.With(
            factory,
            ("Gateway:RestApi:DataEndpoint:Enabled", "false"),
            ("Gateway:Mcp:Tools:Query", "false"),
            ("Gateway:Mcp:Tools:Api", "false"),
            ("Gateway:Mcp:DisabledTools:0", "list_metrics"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task tools_list_shows_the_reduced_set_and_leaks_no_disabled_name()
    {
        await using var client = await ReadPathHost.ConnectAsync(_factory);

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToList();

        names.ShouldContain("list_datasets");
        names.ShouldContain("describe_dataset");
        foreach (var gone in QueryTools.Concat(ApiTools).Append("list_metrics")) names.ShouldNotContain(gone);

        var serialized = JsonSerializer.Serialize(tools.Select(t => new { t.Name, t.Description, t.JsonSchema }));
        foreach (var gone in QueryTools.Concat(ApiTools).Append("list_metrics")) serialized.ShouldNotContain($"\"Name\":\"{gone}\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task C_115_8_Production_wiring_hands_the_one_snapshot_to_registry_dispatcher_and_resource_provider()
    {
        var switches = _factory.Services.GetRequiredService<IReadPathSwitches>();
        using var scope = _factory.Services.CreateScope();

        switches.RestDataEndpointEnabled.ShouldBeFalse();
        ReferenceEquals(switches, scope.ServiceProvider.GetRequiredService<IReadPathSwitches>()).ShouldBeTrue();

        var registry = scope.ServiceProvider.GetRequiredService<IMcpToolRegistry>();
        registry.FindTool("query_sql").ShouldBeNull();
        registry.FindTool("invoke_api").ShouldBeNull();
        registry.FindTool("list_datasets").ShouldNotBeNull();
        registry.FindTool("list_metrics").ShouldBeNull();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IApiDispatcherService>();
        (await dispatcher.InvokeApiAsync("/api/v1/statement", "POST", new Dictionary<string, object?> { ["sql"] = "select 1" })).ShouldContain("EndpointNotFound");
        (await dispatcher.GetEndpointsDocumentationAsync()).ShouldNotContain("/api/v1/data");

        var resources = scope.ServiceProvider.GetRequiredService<IMcpResourceProvider>();
        resources.IsResourceEnabled("autheris://api/openapi.json").ShouldBeFalse();
        resources.IsResourceEnabled("autheris://catalog/summary").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_10_A_disabled_tool_call_over_the_SDK_equals_a_never_registered_name()
    {
        await using var client = await ReadPathHost.ConnectAsync(_factory);

        var disabled = await client.CallToolAsync("query_sql", new Dictionary<string, object?> { ["sql"] = "select 1" });
        var unknown = await client.CallToolAsync("zzz_never_registered", new Dictionary<string, object?> { ["sql"] = "select 1" });

        disabled.IsError.ShouldBe(true);
        disabled.IsError.ShouldBe(unknown.IsError);
        Normalise(disabled, "query_sql").ShouldBe(Normalise(unknown, "zzz_never_registered"));
        Normalise(disabled, "query_sql").ShouldContain("is not registered or allowed");
    }

    private static string Normalise(CallToolResult r, string name) =>
        string.Join("|", r.Content.OfType<TextContentBlock>().Select(t => t.Text.Replace(name, "<NAME>", StringComparison.Ordinal)));

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_11_Disabled_api_resources_and_prompts_answer_like_unknown_ones_and_are_unlisted()
    {
        await using var client = await ReadPathHost.ConnectAsync(_factory);

        var resources = (await client.ListResourcesAsync()).Select(r => r.Uri).ToList();
        resources.ShouldContain("autheris://catalog/summary");
        resources.ShouldNotContain(u => u.StartsWith("autheris://api/", StringComparison.Ordinal));

        var disabled = await Should.ThrowAsync<McpException>(async () => await client.ReadResourceAsync("autheris://api/openapi.json"));
        var unknown = await Should.ThrowAsync<McpException>(async () => await client.ReadResourceAsync("autheris://api/unknown-xyz.json"));
        disabled.Message.Replace("autheris://api/openapi.json", "<URI>", StringComparison.Ordinal)
            .ShouldBe(unknown.Message.Replace("autheris://api/unknown-xyz.json", "<URI>", StringComparison.Ordinal));

        // explore_dataset needs Catalog and Query: unlisted while Query is off; audit_access_compliance (Access, Catalog) stays.
        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();
        prompts.ShouldNotContain("explore_dataset");
        prompts.ShouldContain("audit_access_compliance");
        var gated = await Should.ThrowAsync<McpException>(async () => await client.GetPromptAsync("explore_dataset", new Dictionary<string, object?> { ["dataset"] = "a.b.c" }));
        var missing = await Should.ThrowAsync<McpException>(async () => await client.GetPromptAsync("no_such_prompt", new Dictionary<string, object?> { ["dataset"] = "a.b.c" }));
        gated.Message.Replace("explore_dataset", "<P>", StringComparison.Ordinal).ShouldBe(missing.Message.Replace("no_such_prompt", "<P>", StringComparison.Ordinal));
    }
}

/// <summary>Documentation resources with only Query and REST off (Api on): no disabled path or tool name in any rendered text.</summary>
public sealed class ReadPathMcpDocumentationLeakIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadPathMcpDocumentationLeakIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = ReadPathHost.With(factory, ("Gateway:RestApi:DataEndpoint:Enabled", "false"), ("Gateway:Mcp:Tools:Query", "false"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_13_Documentation_resources_contain_no_disabled_path_or_tool_name()
    {
        await using var client = await ReadPathHost.ConnectAsync(_factory);

        var texts = new List<string>();
        foreach (var uri in new[] { "autheris://api/docs/endpoints", "autheris://api/docs/mcp-tools", "autheris://api/openapi.json" })
        {
            var result = await client.ReadResourceAsync(uri);
            texts.AddRange(result.Contents.OfType<TextResourceContents>().Select(c => c.Text));
        }

        var all = string.Join("\n", texts);
        all.ShouldNotBeEmpty();
        var at = all.IndexOf("/api/v1/data", StringComparison.OrdinalIgnoreCase);
        (at < 0).ShouldBeTrue(at < 0 ? "" : "context: " + all[Math.Max(0, at - 60)..Math.Min(all.Length, at + 100)]);
        // Definitions and schemas of the closed tools are gone. (Prose cross-references in the descriptions of other tools, such as
        // "query with query_graphql", are static product text and a documented residual, PLAN-SEC-115 R-4.)
        foreach (var gone in new[] { "query_sql", "query_graphql", "sample_rows", "query_dataset" })
        {
            all.ShouldNotContain($"### `{gone}`");
            all.ShouldNotContain($"\"name\": \"{gone}\"");
        }

        all.ShouldContain("### `list_datasets`");
    }
}

/// <summary>ST-115-20: an async job cannot read through a closed WebSQL switch (the SQL gate is the rewriter, not the endpoint).</summary>
public sealed class ReadPathAsyncJobWebSqlOffIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "autheris-rps-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ReadPathAsyncJobWebSqlOffIntegrationTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_workDir);
        var dbPath = Path.Combine(_workDir, "data.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE rps_orders (id INTEGER, tenant_id TEXT, name TEXT); INSERT INTO rps_orders VALUES (1,'acme','visible-row');";
            cmd.ExecuteNonQuery();
        }

        _factory = ReadPathHost.With(
            factory,
            ("Gateway:Insecure:danger_bypass_consent_checks", "true"),
            ("Gateway:OpenSchema", "true"),
            ("Gateway:Rebac:Enabled", "false"),
            ("Gateway:DataSources:Connections:default:Provider", "Sqlite"),
            ("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dbPath}"),
            ("Gateway:WebSql:Enabled", "false"),
            ("Gateway:WebSql:DefaultDataSourceName", "default"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_20_With_WebSql_off_an_async_job_does_not_return_rows()
    {
        var client = ReadPathHost.Authenticated(_factory);
        var submit = await client.PostAsJsonAsync("/api/v1/jobs/query", new { query = "SELECT id, name FROM main.rps_orders", format = "json" });
        if (submit.StatusCode != HttpStatusCode.Accepted)
        {
            // Refusing at submission is as closed as failing in the worker.
            (await submit.Content.ReadAsStringAsync()).ShouldNotContain("visible-row");
            return;
        }

        using var accepted = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;

        string? terminal = null;
        for (var attempt = 0; attempt < 80 && terminal is null; attempt++)
        {
            var status = await client.GetAsync($"/api/v1/jobs/{jobId}/status");
            var text = await status.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(text);
            var state = json.RootElement.GetProperty("state").ToString();
            if (state is "Failed" or "failed" or "3" or "Completed" or "completed" or "2" or "Cancelled" or "4")
            {
                terminal = state;
            }
            else
            {
                await Task.Delay(100);
            }
        }

        terminal.ShouldNotBeNull("the job reached no terminal state in time");
        terminal.ShouldBeOneOf("Failed", "failed", "3");
        var result = await client.GetAsync($"/api/v1/jobs/{jobId}/result");
        (await result.Content.ReadAsStringAsync()).ShouldNotContain("visible-row");
    }
}

internal sealed class RestDataCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _count;

    public RestDataCounter()
    {
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Name == "autheris_disabled_path_requests_total") l.EnableMeasurementEvents(inst);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "path" && (t.Value as string) == "rest_data")) Interlocked.Add(ref _count, value);
        });
        _listener.Start();
    }

    public long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}
