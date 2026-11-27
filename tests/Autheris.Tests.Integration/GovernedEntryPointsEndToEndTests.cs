using Autheris.Tests.Common;
namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;

/// <summary>
/// WP-X1 (M-7, plan section 28.2): every governed SQL-text entry point reaches the compiler and the binder, proven through the real DI
/// container against a real SQLite file with two tenants: the caller of tenant <c>acme</c> receives acme rows only, whichever door the
/// statement came through (WebSQL JSON, declared SQL endpoint, REST dataset, Arrow export, async job). The text
/// <c>other-row</c> belongs to another tenant and must never appear in any answer. The per-dialect proof is
/// <see cref="GovernedDialectEndToEndBase"/>.
/// </summary>
public sealed class GovernedEntryPointsEndToEndTests : IClassFixture<GovernedEntryPointsEndToEndTests.HostFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GovernedEntryPointsEndToEndTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        private readonly string _workDir;
        private readonly string _queries;
        private readonly string _dbPath;

        public HostFixture()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "autheris-x1ep-" + Guid.NewGuid().ToString("N"));
            _queries = Path.Combine(_workDir, "queries");
            Directory.CreateDirectory(_queries);
            _dbPath = PostgresDataSource.Create("x1ep", """
                CREATE TABLE ep_orders (id INTEGER, tenant_id TEXT, name TEXT);
                INSERT INTO ep_orders VALUES (1,'acme','acme-row-1'),(2,'acme','acme-row-2'),(3,'other','other-row-3');
                """);

            File.WriteAllText(Path.Combine(_queries, "ep_orders_list.sql"),
                "-- @name ep_orders_list\n-- @datasource default\n-- @summary orders\n-- @method GET\n\nSELECT id, name FROM main.ep_orders ORDER BY id");
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
                ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "1000",
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("GovernedEntryPointsEndToEndTests"),
                ["Gateway:Insecure:danger_bypass_consent_checks"] = "true",
                ["Gateway:DataSources:Connections:default:Provider"] = "PostgreSql",
                ["Gateway:DataSources:Connections:default:DefaultSchema"] = "main",
                ["Gateway:DataSources:Connections:default:ConnectionString"] = _dbPath,
                ["Gateway:WebSql:Enabled"] = "true",
                ["Gateway:WebSql:DefaultDataSourceName"] = "default",
                ["Gateway:WebSql:MaxListParameterValues"] = "3",
                ["Gateway:SqlEndpoints:Enabled"] = "true",
                ["Gateway:SqlEndpoints:Directory"] = _queries,
                ["Gateway:SqlEndpoints:EnableHotReload"] = "false",
                ["Gateway:SqlEndpoints:AutoSyncFromDbt"] = "false",
                ["Gateway:Mcp:Enabled"] = "true",
                ["Gateway:Mcp:EndpointPath"] = "/mcp",
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
            try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
        }
    }

    private async Task RegisterAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "ep_orders", TableName = "ep_orders", SchemaName = "main", SourceType = "PostgreSql", SourceName = "default" },
            Identifier = new TableIdentifier("default", "main", "ep_orders"),
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "name", DataType = "varchar" }
            ]
        });
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-X1-EP");
        c.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        c.DefaultRequestHeaders.Add("X-Test-Tenant", "acme");
        return c;
    }


    private const string CallerSid = "S-1-5-X1-EP";
    private const string DataSourceName = "default";

    /// <summary>
    /// L-9: the audit record of an executed governed statement of THIS caller on THIS data source (the actor and the data source of the
    /// request, the allowed outcome, and the compiled-statement fields). Another actor, another data source, a denied statement or a record
    /// without the compiled fields (for example the denied probe of a door test) cannot vouch for the door under test.
    /// </summary>
    private static bool IsCompiledAuditOfTheCaller(AuditLogEntry e) =>
        e.EventType == Autheris.Domain.Audit.AuditEventTypes.WebSqlQuery &&
        e.Decision == "ALLOW" &&
        e.ActorSid.Value == CallerSid &&
        e.TargetTable == DataSourceName &&
        e.DetailsJson != null &&
        e.DetailsJson.Contains("\"compiler_version\"", StringComparison.Ordinal) &&
        e.DetailsJson.Contains("\"sql.compiled_digest\"", StringComparison.Ordinal);

    /// <summary>
    /// L-A2: the outcome (acme rows only) does not prove the path. A compiled statement that ran through the executor leaves an audit
    /// record with the compiler version and the keyed digest of the compiled text; only Compile plus the executor write both.
    /// </summary>
    private async Task<IReadOnlyList<AuditLogEntry>> CompiledAuditsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        return (await repo.GetAuditLogEntriesAsync(1000)).Where(IsCompiledAuditOfTheCaller).ToList();
    }

    private async Task<int> CompiledAuditCountAsync() => (await CompiledAuditsAsync()).Count;

    private async Task AssertCompiledAuditAsync(int before, string door)
    {
        IReadOnlyList<AuditLogEntry> audits = await CompiledAuditsAsync();
        try
        {
            await WaitHelper.WaitUntil(
                async () =>
                {
                    audits = await CompiledAuditsAsync();
                    return audits.Count > before;
                },
                TimeSpan.FromSeconds(10),
                pollInterval: TimeSpan.FromMilliseconds(250));
        }
        catch (TimeoutException)
        {
            // fall through: the assertion below reports the missing audit record
        }

        audits.Count.ShouldBeGreaterThan(before, $"{door}: no audit record of the caller on '{DataSourceName}' with compiler_version and sql.compiled_digest was written");

        foreach (var entry in audits)
        {
            var details = JsonDocument.Parse(entry.DetailsJson!).RootElement;
            details.GetProperty("compiler_version").GetString().ShouldNotBeNullOrWhiteSpace(door);
            details.GetProperty("dataSource").GetString().ShouldBe(DataSourceName, door);
            var digest = details.GetProperty("sql.compiled_digest").GetString();
            digest.ShouldNotBeNullOrWhiteSpace(door);
            digest!.Length.ShouldBe(64, $"{door}: the digest is a hex HMAC-SHA256");
        }
    }

    private static void ShouldBeAcmeOnly(string text, string where)
    {
        text.ShouldContain("acme-row-1", customMessage: where);
        text.ShouldContain("acme-row-2", customMessage: where);
        text.ShouldNotContain("other-row", customMessage: where);
    }

    [Fact]
    public async Task WebSqlJson_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        var response = await Client().PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id, name FROM main.ep_orders ORDER BY id" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        ShouldBeAcmeOnly(body, "WebSQL JSON");
        await AssertCompiledAuditAsync(audited, "WebSQL JSON");
    }

    [Fact]
    public async Task WebSqlClientParameter_IsBoundByName()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        var response = await Client().PostAsJsonAsync("/api/v1/sql", new
        {
            sql = "SELECT id, name FROM main.ep_orders WHERE name = @n",
            parameters = new Dictionary<string, object?> { ["n"] = "other-row-3" }   // another tenant's row: still invisible
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldNotContain("other-row");
        await AssertCompiledAuditAsync(audited, "WebSQL client parameter");
    }

    [Fact]
    public async Task DeclaredSqlEndpoint_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        var response = await Client().GetAsync("/api/v1/queries/ep_orders_list");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        ShouldBeAcmeOnly(body, "declared SQL endpoint");
        await AssertCompiledAuditAsync(audited, "declared SQL endpoint");
    }

    [Fact]
    public async Task RestDataset_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        var response = await Client().GetAsync("/api/v1/data/default/main/ep_orders");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        ShouldBeAcmeOnly(body, "REST dataset");
        await AssertCompiledAuditAsync(audited, "REST dataset");
    }

    [Fact]
    public async Task ArrowExport_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        var response = await Client().PostAsJsonAsync("/api/v1/export/arrow", new { sql = "SELECT id, name FROM main.ep_orders ORDER BY id" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        text.ShouldContain("acme-row-1");
        text.ShouldNotContain("other-row");
        await AssertCompiledAuditAsync(audited, "Arrow export");
    }

    [Fact]
    public async Task AsyncQueryJob_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();
        var client = Client();

        var submit = await client.PostAsJsonAsync("/api/v1/jobs/query", new { query = "SELECT id, name FROM main.ep_orders ORDER BY id", format = "json" });
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submit.Content.ReadAsStringAsync());
        using var accepted = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = accepted.RootElement.GetProperty("jobId").GetString()!;

        string? result = null;
        for (var attempt = 0; attempt < 60 && result is null; attempt++)
        {
            var status = await client.GetAsync($"/api/v1/jobs/{jobId}/status");
            using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var state = statusJson.RootElement.GetProperty("state").ToString();
            if (state is "Completed" or "2" or "completed")
            {
                result = await (await client.GetAsync($"/api/v1/jobs/{jobId}/result")).Content.ReadAsStringAsync();
            }
            else if (state is "Failed" or "failed")
            {
                throw new InvalidOperationException("The job failed: " + statusJson.RootElement.GetRawText());
            }
            else
            {
                await Task.Delay(250);
            }
        }

        result.ShouldNotBeNull("the job did not complete in time");
        ShouldBeAcmeOnly(result, "async query job");
        await AssertCompiledAuditAsync(audited, "async query job");
    }

    /// <summary>
    /// L-A3: the executor requires a provider execution guard (no silent pass-through). The real host must hand it the SQLite guard, both
    /// as the registered service and inside the governed service that every door uses.
    /// </summary>
    [Fact]
    public void ProviderExecutionGuard_IsThePassThroughGuard_InTheRealHost()
    {
        using var scope = _factory.Services.CreateScope();

        var registered = scope.ServiceProvider.GetRequiredService<Autheris.Application.Sql.Interfaces.IProviderExecutionGuard>();
        registered.ShouldBeOfType<Autheris.Application.Sql.Interfaces.PassThroughExecutionGuard>();

        var service = scope.ServiceProvider.GetRequiredService<Autheris.Application.Sql.Interfaces.IGovernedSqlExecutionService>();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var executor = service.GetType().GetField("_executor", flags)!.GetValue(service)!;
        var guard = executor.GetType().GetField("_executionGuard", flags)!.GetValue(executor);
        guard.ShouldBeOfType<Autheris.Application.Sql.Interfaces.PassThroughExecutionGuard>(
            "the governed service must use the pass-through guard now that the SQLite guard is removed");
    }

    [Fact]
    public async Task WebSqlAsyncStatement_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();
        var client = Client();
        client.DefaultRequestHeaders.Add("X-Trino-User", "x1-ep");

        // The Trino protocol route runs through WebSqlStatementManager (async statements); follow nextUri until the rows arrive.
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/statement")
        {
            Content = new StringContent("SELECT id, name FROM main.ep_orders ORDER BY id", Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Trino-Wait-Timeout", "10s");
        var response = await client.SendAsync(request);
        var text = new StringBuilder(await response.Content.ReadAsStringAsync());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text.ToString());

        for (var hop = 0; hop < 40 && !text.ToString().Contains("acme-row-1", StringComparison.Ordinal); hop++)
        {
            using var json = JsonDocument.Parse(text.ToString());
            text.Append(' ');
            if (!json.RootElement.TryGetProperty("nextUri", out var next) || next.GetString() is not { Length: > 0 } uri)
            {
                break;
            }

            var follow = new HttpRequestMessage(HttpMethod.Get, uri);
            follow.Headers.Add("X-Trino-Wait-Timeout", "10s");
            var followed = await client.SendAsync(follow);
            followed.StatusCode.ShouldBe(HttpStatusCode.OK);
            text.Clear().Append(await followed.Content.ReadAsStringAsync());
        }

        ShouldBeAcmeOnly(text.ToString(), "WebSQL async statement");
        await AssertCompiledAuditAsync(audited, "WebSQL async statement");
    }

    [Fact]
    public async Task McpSqlTool_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        await using var mcp = await McpTestClient.ConnectAsync(Client());
        var (isError, text) = await McpTestClient.CallAsync(mcp, Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql,
            new { sql = "SELECT id, name FROM main.ep_orders ORDER BY id" });

        isError.ShouldBeFalse(text);
        ShouldBeAcmeOnly(text, "MCP SQL tool");
        await AssertCompiledAuditAsync(audited, "MCP SQL tool");
    }

    [Fact]
    public async Task McpDatasetTool_ReturnsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();

        await using var mcp = await McpTestClient.ConnectAsync(Client());
        var (isError, text) = await McpTestClient.CallAsync(mcp, Autheris.Application.Mcp.Services.McpDatasetTools.QueryDataset,
            new { dataset = "default.main.ep_orders", limit = 10 });

        isError.ShouldBeFalse(text);
        ShouldBeAcmeOnly(text, "MCP dataset tool");
        await AssertCompiledAuditAsync(audited, "MCP dataset tool");
    }

    [Fact]
    public async Task ARejectedStatement_AnswersAStableCode_AtEveryDoor()
    {
        await RegisterAsync();
        var client = Client();

        // a reserved gateway identifier is a 400 with its own code (Phase 5 L3)
        var reserved = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id AS autheris_x FROM main.ep_orders" });
        reserved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reserved.Content.ReadAsStringAsync()).ShouldContain("SQL_RESERVED_IDENTIFIER");

        // an uncatalogued table gets the one "not usable" code
        var unknown = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM main.not_catalogued" });
        unknown.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await unknown.Content.ReadAsStringAsync()).ShouldContain("SQL_POLICY_DENIED");
    }

    private async Task<(HttpStatusCode Status, string Body)> WebSqlAsync(string sql, Dictionary<string, object?>? parameters)
    {
        var response = await Client().PostAsJsonAsync("/api/v1/sql", new { sql, parameters });
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string? McpErrorCode(string text)
    {
        if (!text.TrimStart().StartsWith('{'))
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static int[] RowIds(string text)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpSqlToolParameters_ReturnSameRowsAsWebSql()
    {
        await RegisterAsync();
        await using var mcp = await McpTestClient.ConnectAsync(Client());
        var cases = new (string Sql, Dictionary<string, object?> Parameters)[]
        {
            ("SELECT id, name FROM main.ep_orders WHERE name = @n ORDER BY id", new() { ["n"] = "acme-row-1" }),
            ("SELECT id, name FROM main.ep_orders WHERE id IN (@ids) ORDER BY id", new() { ["ids"] = new[] { 1, 2, 3 } })
        };

        foreach (var (sql, parameters) in cases)
        {
            var web = await WebSqlAsync(sql, parameters);
            web.Status.ShouldBe(HttpStatusCode.OK, web.Body);
            var (isError, text) = await McpTestClient.CallAsync(mcp, Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql, new { sql = sql, parameters });

            isError.ShouldBeFalse(text);
            text.ShouldNotContain("other-row");
            using var webDoc = System.Text.Json.JsonDocument.Parse(web.Body);
            var webIds = webDoc.RootElement.GetProperty("rows").EnumerateArray()
                .Select(r => r.ValueKind == JsonValueKind.Array ? r[0].GetInt32() : r.GetProperty("id").GetInt32()).ToArray();
            webIds.ShouldNotBeEmpty();
            RowIds(text).ShouldBe(webIds, sql);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpSqlToolParameters_ErrorCodesEqualWebSql()
    {
        await RegisterAsync();
        await using var mcp = await McpTestClient.ConnectAsync(Client());
        var cases = new (string Sql, Dictionary<string, object?>? Parameters, string Code)[]
        {
            ("SELECT id FROM main.ep_orders WHERE id = @l", new() { ["l"] = new[] { 1, 2 } }, "SQL_LIST_PARAM_POSITION"),
            ("SELECT id FROM main.ep_orders WHERE id IN (@l)", new() { ["l"] = new[] { 1, 2, 3, 4 } }, "SQL_LIST_PARAM_TOO_LARGE"),
            ("SELECT id FROM main.ep_orders WHERE name = 'a\\b'", null, "SQL_LITERAL_NOT_PERMITTED")
        };

        foreach (var (sql, parameters, code) in cases)
        {
            var web = await WebSqlAsync(sql, parameters);
            web.Status.ShouldNotBe(HttpStatusCode.OK, sql);
            web.Body.ShouldContain(code, customMessage: sql);

            var (isError, text) = await McpTestClient.CallAsync(mcp, Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql, new { sql = sql, parameters });
            isError.ShouldBeTrue(text);
            McpErrorCode(text).ShouldBe(code, text);
        }
    }

    [Fact]
    public async Task McpSqlToolParameters_UnsuppliedPlaceholder_IsRefusedLikeWebSql()
    {
        await RegisterAsync();
        await using var mcp = await McpTestClient.ConnectAsync(Client());
        const string sql = "SELECT id FROM main.ep_orders WHERE name = @n";
        var parameters = new Dictionary<string, object?> { ["other"] = "x" };

        var web = await WebSqlAsync(sql, parameters);
        web.Status.ShouldNotBe(HttpStatusCode.OK, web.Body);

        var (isError, text) = await McpTestClient.CallAsync(mcp, Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql, new { sql = sql, parameters });
        isError.ShouldBeTrue(text);
        text.ShouldNotContain("acme-row");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpSqlToolParameters_CursorBindsParameters()
    {
        await RegisterAsync();
        await using var mcp = await McpTestClient.ConnectAsync(Client());
        const string sql = "SELECT id, name FROM main.ep_orders WHERE name <> @n ORDER BY id";
        var tool = Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql;
        var parameters = new Dictionary<string, object?> { ["n"] = "zzz" };

        var (firstError, first) = await McpTestClient.CallAsync(mcp, tool, new { sql, parameters, page = new { size = 1 } });
        firstError.ShouldBeFalse(first);
        RowIds(first).ShouldBe([1]);
        using var firstDoc = System.Text.Json.JsonDocument.Parse(first);
        var cursor = firstDoc.RootElement.GetProperty("next").GetString()!;

        var (secondError, second) = await McpTestClient.CallAsync(mcp, tool, new { sql, parameters, page = new { size = 1 }, next = cursor });
        secondError.ShouldBeFalse(second);
        RowIds(second).ShouldBe([2]);

        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        var (_, tamperedText) = await McpTestClient.CallAsync(mcp, tool, new { sql, parameters, page = new { size = 1 }, next = tampered });
        var invalidCode = McpErrorCode(tamperedText);
        invalidCode.ShouldNotBeNull(tamperedText);

        var (changedError, changed) = await McpTestClient.CallAsync(mcp, tool,
            new { sql, parameters = new Dictionary<string, object?> { ["n"] = "yyy" }, page = new { size = 1 }, next = cursor });
        changedError.ShouldBeTrue(changed);
        McpErrorCode(changed).ShouldBe(invalidCode);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpSqlToolParameters_TenantScope_EqualWebSql()
    {
        await RegisterAsync();
        await using var mcp = await McpTestClient.ConnectAsync(Client());
        var tool = Autheris.Application.Mcp.Services.McpDatasetTools.QuerySql;

        // the other tenant's row (id 3) is never returned, by name or inside an IN list, and WebSQL answers the same
        var byName = new Dictionary<string, object?> { ["n"] = "other-row-3" };
        var byIds = new Dictionary<string, object?> { ["ids"] = new[] { 3 } };
        var cases = new (string Sql, Dictionary<string, object?> Parameters)[]
        {
            ("SELECT id, name FROM main.ep_orders WHERE name = @n", byName),
            ("SELECT id, name FROM main.ep_orders WHERE id IN (@ids)", byIds)
        };

        foreach (var (sql, parameters) in cases)
        {
            var web = await WebSqlAsync(sql, parameters);
            web.Status.ShouldBe(HttpStatusCode.OK, web.Body);
            var (isError, text) = await McpTestClient.CallAsync(mcp, tool, new { sql, parameters });
            isError.ShouldBeFalse(text);
            RowIds(text).ShouldBeEmpty();
            text.ShouldNotContain("other-row");
            web.Body.ShouldNotContain("other-row");
        }
    }
}
