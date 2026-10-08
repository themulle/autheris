namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// MCP dataset tools end to end as an authenticated user with a real consent on a real SQLite table:
/// the agent sees only the granted columns and reads rows through the governed paths.
/// </summary>
public sealed class McpDatasetToolsEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string UserSid = "S-1-5-MCP-E2E-USER";
    private const string Tenant = "tenant_mcp";
    private static readonly TableIdentifier Cranes = new("default", "main", "mcp_cranes");

    private readonly string _dbPath;
    private readonly WebApplicationFactory<Program> _factory;

    public McpDatasetToolsEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "autheris-mcp-e2e-" + Guid.NewGuid().ToString("N") + ".db");
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE mcp_cranes (id INTEGER PRIMARY KEY, serial_number TEXT NOT NULL, crane_type TEXT NOT NULL, tonnage INTEGER NOT NULL, owner_email TEXT);
INSERT INTO mcp_cranes VALUES (1,'LTM-1100','Mobilkran',100,'a@x.test'),(2,'LTM-1230','Mobilkran',230,'b@x.test'),(3,'LR-1600','Raupenkran',600,'c@x.test');";
            cmd.ExecuteNonQuery();
        }

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-McpDatasetE2E-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dbPath}");
            b.UseSetting("Gateway:Mcp:Enabled", "true");
            b.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
        });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task SeedCatalogAndConsentAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var tableId = Guid.NewGuid();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = tableId, DisplayName = "mcp_cranes", TableName = "mcp_cranes", SchemaName = "main", SourceType = "Sqlite", SourceName = "default", Description = "Crane fleet" },
            Identifier = Cranes,
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer", Description = "Key" },
                new() { ColumnName = "serial_number", DataType = "varchar", Description = "Serial number" },
                new() { ColumnName = "crane_type", DataType = "varchar", Description = "Crane type" },
                new() { ColumnName = "tonnage", DataType = "integer", Description = "Lifting capacity in t" },
                new() { ColumnName = "owner_email", DataType = "varchar", IsSensitive = true, Description = "Owner contact" },
            }
        });

        var meta = await repo.GetTableMetadataAsync(Cranes);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = Cranes,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "serial_number", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "crane_type", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "tonnage", AccessLevel = ColumnAccessLevel.Clear }
            ]
        });
    }

    /// <summary>The catalog was written after start-up; rebuild the GraphQL schema now instead of waiting for the refresh timer.</summary>
    private async Task RefreshGraphQlSchemaAsync()
    {
        var module = _factory.Services.GetRequiredService<Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule>();
        var check = typeof(Autheris.GraphQL.Catalog.CatalogGraphQlTypeModule).GetMethod(
            "CheckForCatalogChangesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task<bool>)check.Invoke(module, [System.Threading.CancellationToken.None])!;
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", UserSid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", Tenant);
        return client;
    }

    private static async Task<(bool IsError, JsonElement Payload)> CallAsync(HttpClient client, string tool, object arguments)
    {
        var (isError, text) = await CallRawAsync(client, tool, arguments);
        if (!text.TrimStart().StartsWith('{'))
        {
            throw new InvalidOperationException($"Tool '{tool}' returned: {text}");
        }

        using var content = JsonDocument.Parse(text);
        return (isError, content.RootElement.Clone());
    }

    private static async Task<(bool IsError, string Text)> CallRawAsync(HttpClient client, string tool, object arguments)
    {
        var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        var response = await client.PostAsync("/mcp", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("result");
        return (result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    [Fact]
    public async Task Agent_DiscoversDescribesSamplesAndQueries_OnlyWhatTheConsentAllows()
    {
        await SeedCatalogAndConsentAsync();
        var client = Client();

        var (listError, list) = await CallAsync(client, "list_datasets", new { search = "crane" });
        listError.ShouldBeFalse(list.ToString());
        list.GetProperty("datasets").EnumerateArray().Select(d => d.GetProperty("id").GetString()).ShouldBe(["default.main.mcp_cranes"]);

        var (describeError, description) = await CallAsync(client, "describe_dataset", new { dataset = "default.main.mcp_cranes" });
        describeError.ShouldBeFalse(description.ToString());
        description.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .ShouldBe(["id", "serial_number", "crane_type", "tonnage"]);

        var (sampleError, sample) = await CallAsync(client, "sample_rows", new { dataset = "default.main.mcp_cranes", count = 2 });
        sampleError.ShouldBeFalse(sample.ToString());
        sample.GetProperty("rowCount").GetInt32().ShouldBe(2);
        sample.ToString().ShouldNotContain("x.test");

        // GraphQL is the preferred query path: describe_dataset names the field, query_graphql runs the query.
        var graphQl = description.GetProperty("graphQl");
        var field = graphQl.GetProperty("queryField").GetString()!;
        await RefreshGraphQlSchemaAsync();

        // HotChocolate swaps the rebuilt schema in asynchronously.
        var (exampleError, example) = await CallAsync(client, "query_graphql", new { query = graphQl.GetProperty("exampleQuery").GetString() });
        for (var attempt = 0; exampleError && example.ToString().Contains("does not exist", StringComparison.Ordinal) && attempt < 50; attempt++)
        {
            await Task.Delay(200);
            (exampleError, example) = await CallAsync(client, "query_graphql", new { query = graphQl.GetProperty("exampleQuery").GetString() });
        }

        exampleError.ShouldBeFalse(example.ToString());
        example.GetProperty("data").GetProperty(field).GetArrayLength().ShouldBe(3);

        var (queryError, query) = await CallAsync(client, "query_graphql", new
        {
            query = $$"""query Q($type: String) { {{field}}(where: { crane_type: { eq: $type } }, orderBy: [{ tonnage: DESC }], first: 10) { serial_number tonnage } }""",
            variables = new { type = "Mobilkran" }
        });
        queryError.ShouldBeFalse(query.ToString());
        query.GetProperty("data").GetProperty(field).EnumerateArray().Select(r => r.GetProperty("serial_number").GetString()).ShouldBe(["LTM-1230", "LTM-1100"]);

        var (hiddenError, hidden) = await CallAsync(client, "query_graphql", new { query = $"{{ {field}(first: 10) {{ serial_number owner_email }} }}" });
        hidden.ToString().ShouldNotContain("x.test");

        // Mutations never reach the executor: the guardrail cannot map them to catalog tables and fails closed.
        var (mutationError, mutation) = await CallRawAsync(client, "query_graphql", new { query = "mutation { __typename }" });
        mutationError.ShouldBeTrue();
        mutation.ShouldContain("fail-closed");
    }
}
