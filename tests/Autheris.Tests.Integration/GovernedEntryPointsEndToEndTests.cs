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
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// WP-X1 (M-7, plan section 28.2): every governed SQL-text entry point reaches the compiler and the binder, proven through the real DI
/// container against a real SQLite file with two tenants: the caller of tenant <c>acme</c> receives acme rows only, whichever door the
/// statement came through (WebSQL JSON, declared SQL endpoint, REST dataset, Arrow Flight SQL, Arrow export, async job). The text
/// <c>other-row</c> belongs to another tenant and must never appear in any answer. The per-dialect proof is
/// <see cref="GovernedDialectEndToEndBase"/>.
/// </summary>
public sealed class GovernedEntryPointsEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _workDir;
    private readonly WebApplicationFactory<Program> _factory;

    public GovernedEntryPointsEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-x1ep-" + Guid.NewGuid().ToString("N"));
        var queries = Path.Combine(_workDir, "queries");
        Directory.CreateDirectory(queries);
        var dbPath = Path.Combine(_workDir, "data.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE ep_orders (id INTEGER, tenant_id TEXT, name TEXT);
                INSERT INTO ep_orders VALUES (1,'acme','acme-row-1'),(2,'acme','acme-row-2'),(3,'other','other-row-3');
                """;
            cmd.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(queries, "ep_orders_list.sql"),
            "-- @name ep_orders_list\n-- @datasource default\n-- @summary orders\n-- @method GET\n\nSELECT id, name FROM main.ep_orders ORDER BY id");

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-x1ep-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:OpenSchema", "true");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={dbPath}");
            b.UseSetting("Gateway:WebSql:Enabled", "true");
            b.UseSetting("Gateway:WebSql:DefaultDataSourceName", "default");
            b.UseSetting("Gateway:SqlEndpoints:Enabled", "true");
            b.UseSetting("Gateway:SqlEndpoints:Directory", queries);
            b.UseSetting("Gateway:SqlEndpoints:EnableHotReload", "false");
            b.UseSetting("Gateway:SqlEndpoints:AutoSyncFromDbt", "false");
            b.UseSetting("Gateway:Arrow:FlightTicketSigningKey", "0123456789abcdef0123456789abcdef");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    private async Task RegisterAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "ep_orders", TableName = "ep_orders", SchemaName = "main", SourceType = "Sqlite", SourceName = "default" },
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


    /// <summary>
    /// L-A2: the outcome (acme rows only) does not prove the path. A compiled statement that ran through the executor leaves an audit
    /// record with the compiler version and the keyed digest of the compiled text; only Compile plus the executor write both.
    /// </summary>
    private async Task<int> CompiledAuditCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var entries = await repo.GetAuditLogEntriesAsync(1000);
        return entries.Count(e => e.DetailsJson != null && e.DetailsJson.Contains("\"compiler_version\"", StringComparison.Ordinal)
                                                         && e.DetailsJson.Contains("\"sql.compiled_digest\"", StringComparison.Ordinal));
    }

    private async Task AssertCompiledAuditAsync(int before, string door)
    {
        int after = before;
        for (var attempt = 0; attempt < 40 && after <= before; attempt++)
        {
            after = await CompiledAuditCountAsync();
            if (after <= before) await Task.Delay(250);
        }

        after.ShouldBeGreaterThan(before, $"{door}: no audit record with compiler_version and sql.compiled_digest was written");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var newest = (await repo.GetAuditLogEntriesAsync(1000))
            .Where(e => e.DetailsJson != null && e.DetailsJson.Contains("\"sql.compiled_digest\"", StringComparison.Ordinal))
            .Select(e => JsonDocument.Parse(e.DetailsJson!).RootElement.Clone())
            .ToList();
        newest.ShouldNotBeEmpty();
        foreach (var details in newest)
        {
            details.GetProperty("compiler_version").GetString().ShouldNotBeNullOrWhiteSpace(door);
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
    public async Task ArrowFlightSql_ValidatesThroughCompile_AndStreamsTheCallersTenantOnly()
    {
        await RegisterAsync();
        int audited = await CompiledAuditCountAsync();
        var client = Client();

        // G-6: GetFlightInfo validates through the governed pipeline (no SQL text comes back); a denied table never gets a ticket
        var denied = await client.PostAsJsonAsync("/api/v1/flight/sql/info", new { query = "SELECT id FROM main.not_catalogued" });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).ShouldContain("SQL_POLICY_DENIED");

        var info = await client.PostAsJsonAsync("/api/v1/flight/sql/info", new { query = "SELECT id, name FROM main.ep_orders ORDER BY id" });
        info.StatusCode.ShouldBe(HttpStatusCode.OK, await info.Content.ReadAsStringAsync());
        using var infoJson = JsonDocument.Parse(await info.Content.ReadAsStringAsync());
        var ticket = infoJson.RootElement.GetProperty("ticket");

        var stream = await client.PostAsync("/api/v1/flight/sql/stream", new StringContent(ticket.GetRawText(), Encoding.UTF8, "application/json"));
        stream.StatusCode.ShouldBe(HttpStatusCode.OK, await stream.Content.ReadAsStringAsync());
        var bytes = await stream.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(bytes);
        text.ShouldContain("acme-row-1");
        text.ShouldNotContain("other-row");
        await AssertCompiledAuditAsync(audited, "Arrow Flight SQL");
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
            new { query = "SELECT id, name FROM main.ep_orders ORDER BY id" });

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
}
