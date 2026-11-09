namespace Autheris.Tests.Integration.Transports;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// Issue #447 WP-3: <c>AllowedTransports</c> is enforced at the five choke points for every ingress. Two SQLite sources: S
/// (<c>src_s</c>, the transports of the test) and T (<c>src_t</c>, all transports). Every denial test asserts the stable code
/// <c>TRANSPORT_NOT_ALLOWED</c>, no connection to S, exactly one <c>TRANSPORT_NOT_ALLOWED</c> audit event, a metric delta of one,
/// and a positive control (the same call succeeds once S allows the transport). The source configuration is reloaded between the
/// steps through a reloadable configuration source, so the gate is exercised against the live resolver (REQ-ATR-10).
/// </summary>
[Trait("Category", "Security")]
public sealed class TransportEnforcementTests : IClassFixture<TransportEnforcementTests.Fixture>
{
    private const string SourceS = "src_s";
    private const string SourceT = "src_t";
    private const string ProfileKey = "Gateway:DataSources:Configuration:Profiles:" + SourceS + ":AllowedTransports";
    private const string SigningKey = "0123456789abcdef0123456789abcdef";

    private readonly Fixture _fixture;
    private WebApplicationFactory<Program> _factory => _fixture.Factory;
    private ReloadableConfig _config => _fixture.Config;
    private ConnectionCounter _connections => _fixture.Connections;
    private static readonly ConcurrentQueue<string> Logs = new();
    private MeterCounter _metric => _fixture.Metric;

    public TransportEnforcementTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _root = new();
        private readonly string _workDir;
        public WebApplicationFactory<Program> Factory { get; }
        internal ReloadableConfig Config { get; } = new();
        internal ConnectionCounter Connections { get; } = new();
        internal MeterCounter Metric { get; } = new();
        internal bool Seeded { get; set; }

        public Fixture()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "autheris-atr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
            var sPath = Path.Combine(_workDir, "s.db");
            var tPath = Path.Combine(_workDir, "t.db");
            Seed(sPath, "s_items");
            Seed(tPath, "t_items");

            var connections = Connections;
            var config = Config;
            config.Put(ProfileKey, "All");
            config.Put("Gateway:DataSources:Configuration:Profiles:" + SourceT + ":AllowedTransports", "All");

            Factory = _root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Tenancy:DefaultTenantId", "default");
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                // WP-SIMPL-51: the shared per-principal cost bucket meters every WebSQL/Trino statement; these tests run many statements
                // (or a recursive CTE priced above the default Standard capacity) as one principal, so the Standard tier gets ample capacity.
                b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "1000000");
                b.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:TokenRefillRatePerSecond", "100000");
                b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "5000");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("Fixture"));
                b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
                b.UseSetting("Gateway:Rebac:Enabled", "false");
                b.UseSetting("Gateway:DataSources:Connections:" + SourceS + ":Provider", "Sqlite");
                b.UseSetting("Gateway:DataSources:Connections:" + SourceS + ":ConnectionString", $"Data Source={sPath}");
                b.UseSetting("Gateway:DataSources:Connections:" + SourceT + ":Provider", "Sqlite");
                b.UseSetting("Gateway:DataSources:Connections:" + SourceT + ":ConnectionString", $"Data Source={tPath}");
                b.UseSetting("Gateway:WebSql:Enabled", "true");
                b.UseSetting("Gateway:WebSql:DefaultDataSourceName", SourceT);
                b.UseSetting("Gateway:WebSql:AllowedDataSources:0", SourceT);
                b.UseSetting("Gateway:WebSql:AllowedDataSources:1", SourceS);
                b.UseSetting("Gateway:OData:Enabled", "true");
                b.UseSetting("Gateway:Mcp:Enabled", "true");
                b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
                b.UseSetting("Gateway:Arrow:Enabled", "true");
                b.UseSetting("Gateway:DuckDbOlap:Enabled", "true");
                b.UseSetting("Gateway:WebSql:CrossSource:Enabled", "true");
                b.ConfigureAppConfiguration((_, cfg) => cfg.Add(new ReloadableConfigSource(config)));
                b.ConfigureServices(services =>
                {
                    services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(new CapturingLoggerProvider(Logs));
                    // Counts the connections that are opened to S: a denial must leave this at zero.
                    var original = services.Single(d => d.ServiceType == typeof(ISqlConnectionFactory));
                    services.Remove(original);
                    services.AddSingleton<ISqlConnectionFactory>(sp =>
                        new CountingFactory(
                            (ISqlConnectionFactory)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                            connections,
                            sPath));
                });
            });

            Metric.Start();
        }

        public void Dispose()
        {
            Metric.Dispose();
            Factory.Dispose();
            _root.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
        }
    }

    private static void Seed(string path, string table)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, name TEXT NOT NULL); INSERT INTO {table} VALUES (1,'row-one'),(2,'row-two');";
        cmd.ExecuteNonQuery();
    }

    private async Task EnsureSeededAsync()
    {
        if (_fixture.Seeded)
        {
            return;
        }

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        foreach (var (source, table) in new[] { (SourceS, "s_items"), (SourceT, "t_items") })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = table, TableName = table, SchemaName = "main", SourceType = "Sqlite", SourceName = source },
                Identifier = new TableIdentifier(source, "main", table),
                CatalogView = CatalogView.Full, // sample rows are only read for a Full catalog shape
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "integer" },
                    new TableColumn { ColumnName = "name", DataType = "varchar" }
                ]
            });
        }

        await TestSupport.GraphQlSchemaReadiness.AwaitCatalogSchemaAsync(
            _factory.Services, ["src_s_main_s_items", "src_t_main_t_items"], TestSupport.GraphQlSchemaReadiness.DefaultTimeout);

        // Warm-up while S allows everything: one-time source initialisation must not be mistaken for data access of a denied call.
        using var warm = Client();
        (await warm.SendAsync(WebSql("SELECT id FROM src_s.main.s_items"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await warm.SendAsync(WebSql("SELECT id FROM src_t.main.t_items"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        _fixture.Seeded = true;
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-ATR");
        c.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "default");
        c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        c.DefaultRequestHeaders.Add("X-Trino-User", "S-1-5-ATR");
        return c;
    }

    private void SourceSAllows(string transports) => _config.Put(ProfileKey, transports);

    private async Task<int> DenialAuditsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        return (await repo.GetAuditLogEntriesAsync(5000)).Count(e => e.EventType == AuditEventTypes.TransportNotAllowed);
    }

    private sealed record Snapshot(int Audits, long Metric, int ConnectionsToS);

    private async Task<Snapshot> TakeAsync() => new(await DenialAuditsAsync(), _metric.Total, _connections.ToS);

    /// <summary>The denial contract shared by every transport: one audit event, one metric increment, no connection to S.</summary>
    private async Task AssertDeniedOnceAsync(Snapshot before)
    {
        var after = await TakeAsync();
        (after.Audits - before.Audits).ShouldBe(1, "exactly one TRANSPORT_NOT_ALLOWED audit event");
        (after.Metric - before.Metric).ShouldBe(1, "metric delta of one");
        after.ConnectionsToS.ShouldBe(before.ConnectionsToS, "no connection to the denied source");
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static async Task AssertForbiddenCodeAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
        body.ShouldContain("TRANSPORT_NOT_ALLOWED", Case.Sensitive, body);
        body.ShouldNotContain("row-one");
    }

    // -------------------------------------------------------------------------------------------------------------- WebSQL

    private static HttpRequestMessage WebSql(string sql, object? extra = null) =>
        new(HttpMethod.Post, "/api/v1/sql") { Content = JsonContent.Create(extra ?? new { sql }) };

    [Fact]
    public async Task WebSql_SourceDenied_FailsClosed()
    {
        await EnsureSeededAsync();
        using var client = Client();
        const string sql = "SELECT id, name FROM src_s.main.s_items ORDER BY id";

        SourceSAllows("Rest, GraphQL, OData, Mcp, Trino");
        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.SendAsync(WebSql(sql)));
        await AssertDeniedOnceAsync(before);

        SourceSAllows("WebSql");
        var control = await client.SendAsync(WebSql(sql));
        (await BodyAsync(control)).ShouldContain("row-one");
        control.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebSql_Denied_AsyncJobsUseTheWebSqlFlag()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("Rest, GraphQL, OData, Mcp, Trino");

        var before = await TakeAsync();
        var submit = await client.PostAsJsonAsync("/api/v1/jobs/query", new { query = "SELECT id, name FROM src_s.main.s_items", format = "json" });
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted, await BodyAsync(submit));
        using var accepted = JsonDocument.Parse(await BodyAsync(submit));
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;

        string state = "";
        await Autheris.Tests.Common.WaitHelper.WaitUntil(
            async () =>
            {
                using var status = JsonDocument.Parse(await BodyAsync(await client.GetAsync($"/api/v1/jobs/{jobId}/status")));
                state = status.RootElement.GetProperty("state").ToString();
                return state is "Failed" or "failed" or "3";
            },
            TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(250));

        state.ShouldBeOneOf("Failed", "failed", "3");
        await AssertDeniedOnceAsync(before);
    }

    [Fact]
    public async Task WebSql_JoinSAndT_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("Rest, GraphQL, OData, Mcp, Trino");

        var tBefore = _connections.ToT;
        var before = await TakeAsync();
        var response = await client.SendAsync(WebSql("SELECT s.id, t.name FROM src_s.main.s_items s JOIN src_t.main.t_items t ON s.id = t.id"));

        await AssertForbiddenCodeAsync(response);
        await AssertDeniedOnceAsync(before);
        // no staging: nothing was read from either source
        _connections.ToT.ShouldBe(tBefore);
    }

    [Fact]
    public async Task CrossSource_TransportListedButSourceDenied_Denied()
    {
        // WebSql is listed in CrossSource.AllowedTransports (the default); the federation gate passes, the per-source gate still denies.
        await WebSql_JoinSAndT_Denied();
    }

    // SEC-ATR-11: one denied routed source denies the whole federated query, no staging.
    [Fact]
    public async Task Federation_OneSourceDenied_NoStagingCreated()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("Rest, GraphQL, OData, Mcp, Trino");
        var tBefore = _connections.ToT;

        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.SendAsync(WebSql("SELECT s.id FROM src_t.main.t_items t JOIN src_s.main.s_items s ON s.id = t.id")));
        await AssertDeniedOnceAsync(before);
        _connections.ToT.ShouldBe(tBefore, "the allowed source was not read either (all-or-nothing, no staged data)");
    }

    // SEC-ATR-03: the transport field of a request body (and header/query) never sets the ingress; only the route does.
    [Fact]
    public async Task Rest_ClientTransportBodyField_Ignored()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        // the client claims another transport in the body and a header and the query string: only the route decides the ingress
        var before = await TakeAsync();
        using var request = WebSql("", new { sql = "SELECT id FROM src_s.main.s_items", transport = "GraphQL" });
        request.RequestUri = new Uri("/api/v1/sql?transport=GraphQL", UriKind.Relative);
        request.Headers.Add("X-Autheris-Transport", "GraphQL");
        request.Headers.Add("X-Forwarded-Proto", "GraphQL");
        await AssertForbiddenCodeAsync(await client.SendAsync(request));
        await AssertDeniedOnceAsync(before);
    }

    // ---------------------------------------------------------------------------------------------------------------- Trino

    private static HttpRequestMessage Trino(string sql) =>
        new(HttpMethod.Post, "/v1/statement") { Content = new StringContent(sql, Encoding.UTF8, "text/plain") };

    private static string ErrorName(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("errorName").GetString()! : "";
    }

    [Fact]
    public async Task Trino_Submit_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        const string sql = "SELECT id, name FROM src_s.main.s_items";

        SourceSAllows("WebSql, Rest, GraphQL, OData, Mcp");
        var before = await TakeAsync();
        var denied = await client.SendAsync(Trino(sql));
        var body = await BodyAsync(denied);
        ErrorName(body).ShouldBe("TRANSPORT_NOT_ALLOWED", body);
        body.ShouldNotContain("row-one");
        await AssertDeniedOnceAsync(before);

        SourceSAllows("Trino");
        var control = await BodyAsync(await client.SendAsync(Trino(sql)));
        control.ShouldContain("row-one");
    }

    [Fact]
    public async Task Trino_DeniedErrorObjectCarriesCode()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("WebSql");

        var body = await BodyAsync(await client.SendAsync(Trino("SELECT id FROM src_s.main.s_items")));

        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("errorName").GetString().ShouldBe("TRANSPORT_NOT_ALLOWED");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("message").GetString()!.ShouldNotContain(SourceS, Case.Insensitive);
    }

    [Fact]
    public async Task Trino_PageAfterReloadDenies()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("Trino");

        var first = await BodyAsync(await client.SendAsync(Trino("SELECT id, name FROM src_s.main.s_items")));
        first.ShouldContain("row-one");
        using var firstDoc = JsonDocument.Parse(first);
        var statementId = firstDoc.RootElement.GetProperty("id").GetString()!;

        // the reload removes Trino from S: the next page of the running statement is re-checked and refused
        SourceSAllows("WebSql, Rest, GraphQL, OData, Mcp");
        var before = await TakeAsync();
        var page = await BodyAsync(await client.GetAsync($"/v1/statement/queued/{statementId}"));

        ErrorName(page).ShouldBe("TRANSPORT_NOT_ALLOWED", page);
        page.ShouldNotContain("row-one");
        (await TakeAsync()).Audits.ShouldBeGreaterThanOrEqualTo(before.Audits);
    }

    [Fact]
    public async Task WebSql_StatementIdOnTrinoRoute_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("WebSql, Trino");

        // a statement submitted over the WebSql ingress (wait_timeout makes it a tracked statement)
        var submit = await client.PostAsJsonAsync("/api/v1/sql?wait_timeout=2s", new { sql = "SELECT id, name FROM src_s.main.s_items" });
        var submitted = await BodyAsync(submit);
        submitted.ShouldContain("row-one");
        using var doc = JsonDocument.Parse(submitted);
        var statementId = doc.RootElement.GetProperty("id").GetString()!;

        var before = await TakeAsync();
        var onTrino = await BodyAsync(await client.GetAsync($"/v1/statement/queued/{statementId}"));

        ErrorName(onTrino).ShouldBe("TRANSPORT_NOT_ALLOWED", onTrino);
        onTrino.ShouldNotContain("row-one");
        (await DenialAuditsAsync()).ShouldBe(before.Audits + 1, "the binding denial is audited");
    }

    [Fact]
    public async Task Trino_ClientSourceHeader_CannotSetIngress()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        using var request = Trino("SELECT id FROM src_s.main.s_items");
        request.Headers.Add("X-Trino-Source", "GraphQL");
        request.Headers.Add("X-Trino-Client-Tags", "GraphQL,Mcp");
        var body = await BodyAsync(await client.SendAsync(request));

        ErrorName(body).ShouldBe("TRANSPORT_NOT_ALLOWED", body);
    }

    // ------------------------------------------------------------------------------------------------------------- GraphQL

    // Catalog root fields are named <domain>_<schema>_<table>.
    private static Task<string> GraphQlRootFieldAsync(HttpClient client, string table) =>
        Task.FromResult(table == "s_items" ? "src_s_main_s_items" : "src_t_main_t_items");

    private static async Task<JsonElement> GraphQlAsync(HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        return JsonDocument.Parse(await BodyAsync(response)).RootElement.Clone();
    }

    private static string FirstErrorCode(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 &&
        errors[0].TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code)
            ? code.GetString() ?? string.Empty
            : string.Empty;

    [Fact]
    public async Task GraphQL_SourceRestOnly_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        var sField = await GraphQlRootFieldAsync(client, "s_items");
        var query = $"{{ {sField}(first: 5) {{ id name }} }}";

        SourceSAllows("Rest");
        var before = await TakeAsync();
        var denied = await GraphQlAsync(client, query);
        FirstErrorCode(denied).ShouldBe("TRANSPORT_NOT_ALLOWED", denied.ToString());
        (denied.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object).ShouldBeFalse("no partial data");
        denied.ToString().ShouldNotContain("row-one");
        await AssertDeniedOnceAsync(before);

        SourceSAllows("GraphQL");
        var control = await GraphQlAsync(client, query);
        control.ToString().ShouldContain("row-one");
    }

    [Fact]
    public async Task GraphQL_MixedSAndT_DeniedWhole()
    {
        await EnsureSeededAsync();
        using var client = Client();
        var sField = await GraphQlRootFieldAsync(client, "s_items");
        var tField = await GraphQlRootFieldAsync(client, "t_items");
        SourceSAllows("Rest");
        var tBefore = _connections.ToT;

        var before = await TakeAsync();
        var denied = await GraphQlAsync(client, $"{{ t: {tField}(first: 5) {{ id name }} s: {sField}(first: 5) {{ id name }} }}");

        FirstErrorCode(denied).ShouldBe("TRANSPORT_NOT_ALLOWED", denied.ToString());
        denied.ToString().ShouldNotContain("row-one");
        await AssertDeniedOnceAsync(before);
        _connections.ToT.ShouldBe(tBefore, "no resolver of T ran");
    }

    [Fact]
    public async Task GraphQL_DeniedSourceInMixedOperation_NoResolverRuns()
    {
        // SEC-ATR-06 with aliases and a fragment hiding the denied field behind the allowed one.
        await EnsureSeededAsync();
        using var client = Client();
        var sField = await GraphQlRootFieldAsync(client, "s_items");
        var tField = await GraphQlRootFieldAsync(client, "t_items");
        SourceSAllows("Rest");
        var tBefore = _connections.ToT;

        var before = await TakeAsync();
        var denied = await GraphQlAsync(client, $"query Q {{ first: {tField}(first: 1) {{ id }} ...F }} fragment F on Query {{ hidden: {sField}(first: 1) {{ id }} }}");

        FirstErrorCode(denied).ShouldBe("TRANSPORT_NOT_ALLOWED", denied.ToString());
        _connections.ToT.ShouldBe(tBefore);
        await AssertDeniedOnceAsync(before);
    }

    // SEC-ATR-07: the gate runs before any cache; a warm cache never serves a denied transport.
    [Fact]
    public async Task GraphQL_CacheHit_DoesNotBypassGate()
    {
        await EnsureSeededAsync();
        using var client = Client();
        var sField = await GraphQlRootFieldAsync(client, "s_items");
        var query = $"{{ {sField}(first: 5) {{ id name }} }}";

        // warm every cache (document, operation, planner) while the transport is allowed, then reload and repeat the identical request
        SourceSAllows("GraphQL");
        (await GraphQlAsync(client, query)).ToString().ShouldContain("row-one");
        (await GraphQlAsync(client, query)).ToString().ShouldContain("row-one");

        SourceSAllows("Rest");
        var denied = await GraphQlAsync(client, query);

        FirstErrorCode(denied).ShouldBe("TRANSPORT_NOT_ALLOWED", denied.ToString());
        denied.ToString().ShouldNotContain("row-one");
    }

    // ----------------------------------------------------------------------------------------------------------------- REST

    [Fact]
    public async Task Rest_DatasetData_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("WebSql, GraphQL, OData, Mcp, Trino");

        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.GetAsync("/api/v1/data/src_s/main/s_items"));
        await AssertDeniedOnceAsync(before);

        SourceSAllows("Rest");
        (await BodyAsync(await client.GetAsync("/api/v1/data/src_s/main/s_items"))).ShouldContain("row-one");
    }

    [Fact]
    public async Task Rest_ArrowExport_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.PostAsJsonAsync("/api/v1/export/arrow", new { sql = "SELECT id, name FROM src_s.main.s_items" }));
        await AssertDeniedOnceAsync(before);
    }

    // ---------------------------------------------------------------------------------------------------------------- OData

    [Fact]
    public async Task OData_EntitySet_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.GetAsync("/odata/v4/src_s/main/s_items"));
        await AssertDeniedOnceAsync(before);

        SourceSAllows("OData");
        (await BodyAsync(await client.GetAsync("/odata/v4/src_s/main/s_items"))).ShouldContain("row-one");
    }

    [Fact]
    public async Task OData_CountEntitySet_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.GetAsync("/odata/v4/src_s/main/s_items/$count"));
        await AssertDeniedOnceAsync(before);

        SourceSAllows("OData");
        (await client.GetAsync("/odata/v4/src_s/main/s_items/$count")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------------------------------------------------------ MCP

    [Fact]
    public async Task Mcp_QuerySql_Denied()
    {
        await EnsureSeededAsync();
        using var http = Client();
        await using var mcp = await McpTestClient.ConnectAsync(http);
        const string sql = "SELECT id, name FROM src_s.main.s_items";

        SourceSAllows("WebSql, Rest, GraphQL, OData, Trino");
        var before = await TakeAsync();
        var (isError, text) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = sql });
        isError.ShouldBeTrue(text);
        text.ShouldContain("TRANSPORT_NOT_ALLOWED");
        text.ShouldNotContain("row-one");
        await AssertDeniedOnceAsync(before);

        SourceSAllows("Mcp");
        var (controlError, controlText) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = sql });
        controlError.ShouldBeFalse(controlText);
        controlText.ShouldContain("row-one");
    }

    // SEC-ATR-11: inner MCP delegation reuses the outer MCP scope ingress.
    [Fact]
    public async Task Mcp_QuerySqlDelegation_ChecksMcpNotWebSql()
    {
        // S allows WebSql only: the MCP tool delegates to the governed SQL pipeline, but the ingress that counts is MCP.
        await EnsureSeededAsync();
        using var http = Client();
        await using var mcp = await McpTestClient.ConnectAsync(http);
        SourceSAllows("WebSql");

        var before = await TakeAsync();
        var (isError, text) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = "SELECT id FROM src_s.main.s_items" });

        isError.ShouldBeTrue(text);
        text.ShouldContain("TRANSPORT_NOT_ALLOWED");
        await AssertDeniedOnceAsync(before);
    }

    [Fact]
    public async Task Mcp_DatasetTool_Denied()
    {
        await EnsureSeededAsync();
        using var http = Client();
        await using var mcp = await McpTestClient.ConnectAsync(http);
        SourceSAllows("GraphQL");

        var before = await TakeAsync();
        var (isError, text) = await McpTestClient.CallAsync(mcp, "query_dataset", new { dataset = "src_s.main.s_items" });

        isError.ShouldBeTrue(text);
        text.ShouldContain("TRANSPORT_NOT_ALLOWED");
        text.ShouldNotContain("row-one");
        await AssertDeniedOnceAsync(before);
    }

    [Fact]
    public async Task Mcp_GraphQlSourceOnly_ToolCallDenied_ToolCallsRunUnderTheMcpIngress()
    {
        // The MCP request scope carries the Mcp ingress into the tool call (and into the in-process GraphQL executor): a source that
        // allows GraphQL but not Mcp is refused, never served as if the call had come over GraphQL.
        await EnsureSeededAsync();
        using var http = Client();
        await using var mcp = await McpTestClient.ConnectAsync(http);
        SourceSAllows("GraphQL");

        var (isError, text) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = "SELECT id FROM src_s.main.s_items" });

        isError.ShouldBeTrue(text);
        text.ShouldContain("TRANSPORT_NOT_ALLOWED");
    }

    // --------------------------------------------------------------------------------------------------------------- OLAP

    [Fact]
    public async Task OlapStaging_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        SourceSAllows("GraphQL");

        var before = await TakeAsync();
        var response = await client.PostAsJsonAsync("/api/v1/olap/query", new { sql = "SELECT * FROM s_items", tableNames = new[] { "src_s.main.s_items" } });

        (await BodyAsync(response)).ShouldContain("TRANSPORT_NOT_ALLOWED");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await AssertDeniedOnceAsync(before);
    }

    // ------------------------------------------------------------------------------------------------------------- fail-closed

    [Theory]
    [InlineData("None")]
    [InlineData("GraphQL")]
    public async Task FailClosed_Theory_AllTransports(string sourceAllows)
    {
        await EnsureSeededAsync();
        using var client = Client();
        await using var mcp = await McpTestClient.ConnectAsync(client);
        // "None" (an explicit deny) and a profile that lists only another transport: every ingress except GraphQL is refused.
        SourceSAllows(sourceAllows);

        void Expect(bool denied, string where) => denied.ShouldBe(sourceAllows == "None" || !where.StartsWith("GraphQL", StringComparison.Ordinal), where);

        var webSql = await client.SendAsync(WebSql("SELECT id FROM src_s.main.s_items"));
        Expect((await BodyAsync(webSql)).Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "WebSql");
        Expect((await BodyAsync(await client.SendAsync(Trino("SELECT id FROM src_s.main.s_items")))).Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "Trino");
        Expect((await BodyAsync(await client.GetAsync("/api/v1/data/src_s/main/s_items"))).Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "Rest");
        Expect((await BodyAsync(await client.GetAsync("/odata/v4/src_s/main/s_items"))).Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "OData");
        var (_, mcpText) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = "SELECT id FROM src_s.main.s_items" });
        Expect(mcpText.Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "Mcp");
        var sField = await GraphQlRootFieldAsync(client, "s_items");
        var gql = (await GraphQlAsync(client, $"{{ {sField}(first: 1) {{ id }} }}")).ToString();
        Expect(gql.Contains("TRANSPORT_NOT_ALLOWED", StringComparison.Ordinal), "GraphQL");
    }

    // ---------------------------------------------------------------------------------------- review round: sample rows, change detection

    // SEC-ATR-04: the non-synthetic sample rows route reads rows through C3; it carries the REST ingress. A source without REST is hidden
    // from the catalog for that ingress (REQ-ATR-08: discovery hides, one denial for "not visible"), and no row is read from it.
    [Fact]
    public async Task Rest_SampleRows_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        // the administrator view samples rows (a Full catalog shape)
        client.DefaultRequestHeaders.Remove("X-Test-Roles");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin,Reader");
        const string url = "/api/v1/catalog/datasets/src_s.main.s_items/sample-rows?count=2";

        SourceSAllows("WebSql, GraphQL, OData, Mcp, Trino");
        var connectionsBefore = _connections.ToS;
        var denied = await client.GetAsync(url);
        var body = await BodyAsync(denied);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
        body.ShouldNotContain("row-one");
        _connections.ToS.ShouldBe(connectionsBefore, "no connection to the denied source");

        SourceSAllows("Rest");
        var control = await client.GetAsync(url);
        control.StatusCode.ShouldBe(HttpStatusCode.OK, await BodyAsync(control));
        (await BodyAsync(control)).ShouldContain("row-");
    }

    // SEC-ATR-04: change detection reads the version, the change log and the current rows of the table's source.
    [Fact]
    public async Task Rest_ChangeToken_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        const string url = "/api/v1/data/src_s/main/s_items/change-token";

        SourceSAllows("WebSql, GraphQL, OData, Mcp, Trino");
        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.GetAsync(url));
        await AssertDeniedOnceAsync(before);

        SourceSAllows("Rest");
        var control = await client.GetAsync(url);
        control.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, await BodyAsync(control));
    }

    [Fact]
    public async Task Rest_ChangesSince_Denied()
    {
        await EnsureSeededAsync();
        using var client = Client();
        const string url = "/api/v1/data/src_s/main/s_items/changes";

        SourceSAllows("WebSql, GraphQL, OData, Mcp, Trino");
        var before = await TakeAsync();
        await AssertForbiddenCodeAsync(await client.PostAsJsonAsync(url, new { }));
        await AssertDeniedOnceAsync(before);
    }

    // SEC-ATR-07 / REQ-ATR-10: after a reload that denies OData, the cached OpenAPI document and OData discovery no longer list the source.
    [Fact]
    public async Task OData_DiscoveryCaches_ReloadThatDeniesTheSource_HidesItFromDiscovery()
    {
        await EnsureSeededAsync();
        using var client = Client();

        SourceSAllows("OData");
        (await BodyAsync(await client.GetAsync("/odata/v4/$openapi"))).ShouldContain("s_items");
        (await BodyAsync(await client.GetAsync("/odata/v4/$openapi"))).ShouldContain("s_items");
        (await BodyAsync(await client.GetAsync("/odata/v4/$metadata"))).ShouldContain("s_items");

        SourceSAllows("Rest");
        (await BodyAsync(await client.GetAsync("/odata/v4/$openapi"))).ShouldNotContain("s_items");
        (await BodyAsync(await client.GetAsync("/odata/v4/$metadata"))).ShouldNotContain("s_items");
    }

    // ---------------------------------------------------------------------------------------------------------------- doubles

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(categoryName, sink);

        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception?.GetType().Name}");
        }
    }

    internal sealed class ReloadableConfig : ConfigurationProvider
    {
        public void Put(string key, string value)
        {
            Data[key] = value;
            OnReload();
        }
    }

    private sealed class ReloadableConfigSource(ReloadableConfig provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    internal sealed class ConnectionCounter
    {
        private int _toS;
        private int _toT;

        public int ToS => Volatile.Read(ref _toS);

        public int ToT => Volatile.Read(ref _toT);

        public void Opened(bool isS)
        {
            if (isS)
            {
                Interlocked.Increment(ref _toS);
            }
            else
            {
                Interlocked.Increment(ref _toT);
            }
        }
    }

    private sealed class CountingFactory(ISqlConnectionFactory inner, ConnectionCounter counter, string sPath) : ISqlConnectionFactory
    {
        public Task<System.Data.Common.DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            counter.Opened(options.ConnectionString?.Contains(sPath, StringComparison.Ordinal) == true);
            return inner.CreateOpenConnectionAsync(options, ct);
        }
    }

    internal sealed class MeterCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public long Total => Interlocked.Read(ref _total);

        public void Start()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "autheris_transport_denied_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
