namespace Autheris.Tests.Integration.Transports;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Model;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Xunit;
using Autheris.Api.Endpoints;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// Issue #447 WP-2a..f: every transport lane stamps its ingress from endpoint metadata only (SEC-ATR-02), and the background
/// re-entry points (statement manager, async job worker, MCP stdio) carry it (SEC-ATR-05). WP-2 stamps only; nothing is denied here.
/// A recording decorator of <see cref="IIngressTransportContext"/> plays the "probe gate".
/// </summary>
public sealed class IngressStampingTests : IClassFixture<IngressStampingTests.Fixture>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Probe _probe;

    public IngressStampingTests(Fixture fixture)
    {
        _factory = fixture.Factory;
        _probe = fixture.Probe;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();
        public Probe Probe { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }

        public Fixture()
        {
            var probe = Probe;
            Factory = _root.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("Fixture"));
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "500");
                builder.UseSetting("Gateway:WebSql:Enabled", "true");
                builder.UseSetting("Gateway:OData:Enabled", "true");
                builder.UseSetting("Gateway:Mcp:Enabled", "true");
                builder.UseSetting("Gateway:Arrow:Enabled", "true");
                builder.UseSetting("Gateway:DuckDbOlap:Enabled", "true");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(probe);
                    services.RemoveAll<IIngressTransportContext>();
                    services.AddScoped<IIngressTransportContext>(sp => new RecordingContext(new IngressTransportContext(), sp.GetRequiredService<Probe>()));

                    // Records the ingress the executing (background) scope sees; throws afterwards so nothing real runs.
                    services.RemoveAll<IGovernedSqlExecutionService>();
                    services.AddScoped<IGovernedSqlExecutionService>(sp =>
                    {
                        var context = sp.GetRequiredService<IIngressTransportContext>();
                        var sub = Substitute.For<IGovernedSqlExecutionService>();
                        sub.ExecuteQueryBufferedAsync(default!, default!, default, default)
                            .ReturnsForAnyArgs<Task<GovernedSqlResult>>(_ =>
                            {
                                probe.Executed.Add(context.Current);
                                probe.FirstExecution.Release();
                                return Task.FromException<GovernedSqlResult>(new InvalidOperationException("probe"));
                            });
                        return sub;
                    });
                });
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            _root.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    public sealed class Probe
    {
        public ConcurrentQueue<DataSourceTransportFlags> Stamped { get; } = new();

        public ConcurrentBag<DataSourceTransportFlags> Executed { get; } = [];

        public SemaphoreSlim FirstExecution { get; } = new(0);
    }

    private sealed class RecordingContext(IIngressTransportContext inner, Probe probe) : IIngressTransportContext
    {
        public DataSourceTransportFlags Current => inner.Current;

        public void Set(DataSourceTransportFlags transport)
        {
            inner.Set(transport);
            probe.Stamped.Enqueue(transport);
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "alice");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "User");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-default");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return client;
    }

    private async Task<DataSourceTransportFlags[]> SendAsync(HttpMethod method, string path, string? json = null, Action<HttpRequestMessage>? configure = null)
    {
        using var client = Client();
        _probe.Stamped.Clear();
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        configure?.Invoke(request);
        using var response = await client.SendAsync(request);
        return _probe.Stamped.ToArray();
    }

    [Fact]
    public async Task WebSqlRoute_StampsIngress_WebSql()
    {
        (await SendAsync(HttpMethod.Post, "/api/v1/sql", "{\"sql\":\"SELECT 1\"}")).ShouldBe([DataSourceTransportFlags.WebSql]);
        (await SendAsync(HttpMethod.Get, "/api/v1/sql/statements/abc")).ShouldBe([DataSourceTransportFlags.WebSql]);
    }

    [Fact]
    public async Task TrinoRoute_StampsIngress_Trino()
    {
        (await SendAsync(HttpMethod.Post, "/v1/statement", "{\"sql\":\"SELECT 1\"}")).ShouldBe([DataSourceTransportFlags.Trino]);
        (await SendAsync(HttpMethod.Get, "/v1/statement/queued/abc")).ShouldBe([DataSourceTransportFlags.Trino]);
    }

    [Fact]
    public async Task AsyncJobRoute_StampsIngress_WebSql()
    {
        (await SendAsync(HttpMethod.Get, $"/api/v1/jobs/{Guid.NewGuid():D}/status")).ShouldBe([DataSourceTransportFlags.WebSql]);
    }

    [Fact]
    public async Task GraphQlRoute_StampsIngress_GraphQL()
    {
        (await SendAsync(HttpMethod.Post, "/graphql", "{\"query\":\"{ __typename }\"}")).ShouldBe([DataSourceTransportFlags.GraphQL]);
    }

    [Fact]
    public async Task McpRoute_StampsIngress_Mcp()
    {
        (await SendAsync(HttpMethod.Post, "/mcp", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")).ShouldBe([DataSourceTransportFlags.Mcp]);
    }

    [Fact]
    public async Task ODataRoute_StampsIngress_OData()
    {
        (await SendAsync(HttpMethod.Get, "/odata/v4/$metadata")).ShouldBe([DataSourceTransportFlags.OData]);
        (await SendAsync(HttpMethod.Get, "/odata/v4/d/s/t/$count")).ShouldBe([DataSourceTransportFlags.OData]);
    }

    [Theory]
    [InlineData("GET", "/api/v1/data/d/s/t")]
    [InlineData("POST", "/api/v1/data/d/s/t/stream")]
    [InlineData("POST", "/api/v1/export/arrow")]
    [InlineData("POST", "/api/v1/analytics/aggregate")]
    [InlineData("GET", "/api/v1/queries/")]
    [InlineData("POST", "/api/v1/olap/query")]
    [InlineData("GET", "/api/v1/catalog/datasets")]
    public async Task RestRoute_StampsIngress_Rest(string method, string path)
    {
        var json = method == "POST" ? "{}" : null;
        (await SendAsync(new HttpMethod(method), path, json)).ShouldBe([DataSourceTransportFlags.Rest]);
    }

    [Fact]
    public async Task McpStdio_StampsMcpPerRequest()
    {
        using var client = Client(); // starts the host
        await using var scope = _factory.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMcpStdioRunner>();
        var context = scope.ServiceProvider.GetRequiredService<IIngressTransportContext>();
        context.Current.ShouldBe(DataSourceTransportFlags.None);

        await runner.RunAsync(
            new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n"),
            new StringWriter(),
            "cli-developer",
            "tenant-default");

        context.Current.ShouldBe(DataSourceTransportFlags.Mcp);
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.Trino)]
    [InlineData(DataSourceTransportFlags.WebSql)]
    public async Task StatementManager_BackgroundScopeCarriesSubmittingIngress(DataSourceTransportFlags submitting)
    {
        using var client = Client(); // starts the host
        var manager = _factory.Services.GetRequiredService<IWebSqlStatementManager>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "test"));
        _probe.Executed.Clear();

        await manager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"), user, new Sid("alice"), new TenantId("tenant-default"), TimeSpan.FromSeconds(5), submitting);

        _probe.Executed.ToArray().ShouldBe([submitting]);
    }

    [Fact]
    public async Task StatementManager_UnstampedSubmit_LeavesBackgroundScopeUnset()
    {
        using var client = Client();
        var manager = _factory.Services.GetRequiredService<IWebSqlStatementManager>();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "bob")], "test"));
        _probe.Executed.Clear();

        await manager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"), user, new Sid("bob"), new TenantId("tenant-default"), TimeSpan.FromSeconds(5));

        _probe.Executed.ToArray().ShouldBe([DataSourceTransportFlags.None]);
    }

    [Fact]
    public async Task AsyncJobWorker_StampsWebSql()
    {
        using var client = Client();
        _probe.Executed.Clear();
        while (_probe.FirstExecution.CurrentCount > 0)
        {
            _probe.FirstExecution.Wait(0);
        }

        using var response = await client.PostAsJsonAsync("/api/v1/jobs/query", new AsyncQueryJobRequest("SELECT 1"));

        await _probe.FirstExecution.WaitAsync(TimeSpan.FromSeconds(30));

        _probe.Executed.ShouldNotBeEmpty($"The job was not executed (submit status {(int)response.StatusCode}).");
        _probe.Executed.ShouldAllBe(f => f == DataSourceTransportFlags.WebSql);
    }

    /// <summary>SEC-ATR-02 / ATR-06: client-controlled headers, query and body never influence the ingress.</summary>
    [Trait("Category", "Security")]
    [Fact]
    public async Task ClientHeader_CannotSetIngress()
    {
        void Spoof(HttpRequestMessage r)
        {
            r.Headers.TryAddWithoutValidation("X-Autheris-Transport", "Mcp");
            r.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "mcp");
            r.Headers.TryAddWithoutValidation("X-Forwarded-Host", "mcp.example");
            r.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/mcp");
            r.Headers.TryAddWithoutValidation("X-Trino-Source", "graphql");
            r.Headers.TryAddWithoutValidation("X-Transport", "OData");
        }

        (await SendAsync(HttpMethod.Post, "/api/v1/sql?transport=GraphQL", "{\"sql\":\"SELECT 1\",\"transport\":\"Mcp\"}", Spoof))
            .ShouldBe([DataSourceTransportFlags.WebSql]);
        (await SendAsync(HttpMethod.Post, "/v1/statement?transport=Mcp", "{\"sql\":\"SELECT 1\",\"transport\":\"WebSql\"}", Spoof))
            .ShouldBe([DataSourceTransportFlags.Trino]);
        (await SendAsync(HttpMethod.Get, "/api/v1/data/d/s/t?transport=OData", null, Spoof))
            .ShouldBe([DataSourceTransportFlags.Rest]);

        // A route without metadata is never stamped, whatever the client sends.
        (await SendAsync(HttpMethod.Get, "/health/live?transport=Rest", null, Spoof)).ShouldBeEmpty();
    }

    /// <summary>SEC-ATR-02: the route table declares exactly one defined ingress per data route; none declares a multi-bit value.</summary>
    [Trait("Category", "Security")]
    [Theory]
    [InlineData("/api/v1/sql", DataSourceTransportFlags.WebSql)]
    [InlineData("/api/sql", DataSourceTransportFlags.WebSql)]
    [InlineData("/api/v1/sql/statements/{statementId}", DataSourceTransportFlags.WebSql)]
    [InlineData("/v1/statement", DataSourceTransportFlags.Trino)]
    [InlineData("/v1/statement/executing/{statementId}", DataSourceTransportFlags.Trino)]
    [InlineData("/api/v1/jobs/query", DataSourceTransportFlags.WebSql)]
    [InlineData("/odata/v4/{domain}/{schema}/{tableName}", DataSourceTransportFlags.OData)]
    [InlineData("/odata/v4/{entitySetName}", DataSourceTransportFlags.OData)]
    [InlineData("/api/v1/data/{datasetId}", DataSourceTransportFlags.Rest)]
    [InlineData("/api/v1/data/{domain}/{schema}/{table}/bulk", DataSourceTransportFlags.Rest)]
    [InlineData("/api/v1/export/arrow", DataSourceTransportFlags.Rest)]
    [InlineData("/api/v1/analytics/metrics/query", DataSourceTransportFlags.Rest)]
    [InlineData("/api/v1/queries/{name}", DataSourceTransportFlags.Rest)]
    [InlineData("/api/v1/olap/query", DataSourceTransportFlags.Rest)]
    public void DataRoutes_DeclareExactlyOneIngress(string pattern, DataSourceTransportFlags expected)
    {
        using var client = Client(); // starts the host
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        endpoints.ShouldNotBeEmpty($"{pattern} is not mapped.");
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IngressTransportMetadata>().Select(m => m.Transport).Distinct().ShouldBe([expected]);
        }
    }
}
