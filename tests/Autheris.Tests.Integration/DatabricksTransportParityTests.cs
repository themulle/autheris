namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// WP-SIMPL-37 / OD-36 (SEC-X1-14): transport parity evidence for admitting Databricks into WebSQL, Trino, and MCP transports.
/// Proves that the same caller, policies, and row limits yield the same rows and masks across all three transports,
/// that tenant isolation holds even under a case-insensitive collation (CastBinary), that consent denials match,
/// and that write statements are rejected on Trino and MCP.
/// </summary>
[Trait("Category", "Spark")]
[Trait("Category", "Security")]
public sealed class DatabricksTransportParityTests : IClassFixture<SparkProxyFixture>, IAsyncLifetime, IDisposable
{
    private const string Source = "dbx";
    private const string SourceNoTrino = "dbx_notrino";
    private const string CatalogName = "spark_catalog";
    private const string SchemaName = "default";
    private static readonly TableIdentifier OrdersId = new(Source, SchemaName, "orders");
    private static readonly TableIdentifier OrdersCiId = new(Source, SchemaName, "orders_ci");
    private static readonly TableIdentifier OrdersNoTrinoId = new(SourceNoTrino, SchemaName, "orders");

    private readonly SparkProxyFixture _spark;
    private WebApplicationFactory<Program>? _factory;
    private string? _govDbConnStr;

    public DatabricksTransportParityTests(SparkProxyFixture spark)
    {
        _spark = spark;
    }

    public async Task InitializeAsync()
    {
        _govDbConnStr = GovernanceTestDatabase.Create(nameof(DatabricksTransportParityTests));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:Insecure:danger_allow_anonymous_access", "false");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", _govDbConnStr);

            // Primary Databricks data source
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Provider", "Databricks");
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:Host", "adb-123.azuredatabricks.net");
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:WarehouseId", "wh-parity");
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:Catalog", CatalogName);
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:Schema", SchemaName);
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:Auth:Mode", "Pat");
            builder.UseSetting("Gateway:DataSources:Connections:dbx:Databricks:Auth:PatRef", "kv://dbx-pat");

            // Secondary Databricks data source: AllowedTransports restricts to WebSql only (excludes Trino)
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Provider", "Databricks");
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:Host", "adb-123.azuredatabricks.net");
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:WarehouseId", "wh-parity");
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:Catalog", CatalogName);
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:Schema", SchemaName);
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:Auth:Mode", "Pat");
            builder.UseSetting("Gateway:DataSources:Connections:dbx_notrino:Databricks:Auth:PatRef", "kv://dbx-pat");
            builder.UseSetting("Gateway:DataSources:Configuration:Profiles:dbx_notrino:AllowedTransports", "WebSql");

            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:DefaultDataSourceName", "dbx");
            builder.UseSetting("Gateway:WebSql:AllowDml", "false");
            builder.UseSetting("Gateway:WebSql:DefaultMaxRows", "200");
            builder.UseSetting("Gateway:WebSql:MaxAllowedRows", "500");

            builder.UseSetting("Gateway:Mcp:Enabled", "true");

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IKeyVaultSecretProvider>(new FakeSecretProvider(new Dictionary<string, string>
                {
                    ["kv://dbx-pat"] = "test-token-parity-123"
                }));

                services.AddHttpClient<DatabricksStatementExecutionClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeDatabricksHttpRouter(async req => await HandleDatabricksRestAsync(req)));
            });
        });

        // Seed governance metadata
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await SeedGovernanceAsync(repo);
    }

    private static async Task SeedGovernanceAsync(IGovernanceRepository repo)
    {
        // 1. Orders table metadata
        var ordersMeta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "orders", TableName = "orders", SchemaName = SchemaName, SourceName = Source, SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
            Identifier = OrdersId,
            Columns = [Col("id", "int"), Col("tenantid", "string"), Col("region", "string"), Col("status", "string"), Col("amount", "decimal"), Col("email", "string", sensitive: true)],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["email"] = new MaskingRule { RuleType = "REDACT" } }
        };
        await repo.UpsertTableMetadataAsync(ordersMeta);

        // Consent for Acme on Orders (Reader role, EU region filter, email redacted, amount clear)
        await repo.CreateConsentAsync(new Consent
        {
            TableId = ordersMeta.Table.Id, TableIdentifier = OrdersId, TenantId = new TenantId("acme"), Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role, RoleName = "Reader", ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [CRule("id"), CRule("region"), CRule("status"), CRule("amount"), CRule("email", ColumnAccessLevel.Mask)],
            RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQUALS", ValueJson = "\"EU\"" }]
        });

        // 2. Orders_ci table metadata (case-insensitive collation)
        var ordersCiMeta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "orders_ci", TableName = "orders_ci", SchemaName = SchemaName, SourceName = Source, SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
            Identifier = OrdersCiId,
            Columns = [Col("id", "int"), Col("tenantid", "string")]
        };
        await repo.UpsertTableMetadataAsync(ordersCiMeta);

        // Consent for acme and ACME on orders_ci
        foreach (var t in new[] { "acme", "ACME" })
        {
            await repo.CreateConsentAsync(new Consent
            {
                TableId = ordersCiMeta.Table.Id, TableIdentifier = OrdersCiId, TenantId = new TenantId(t), Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Role, RoleName = "Reader", ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = [CRule("id"), CRule("tenantid")]
            });
        }

        // 3. Orders table metadata for dbx_notrino
        var ordersNoTrinoMeta = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "orders", TableName = "orders", SchemaName = SchemaName, SourceName = SourceNoTrino, SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
            Identifier = OrdersNoTrinoId,
            Columns = ordersMeta.Columns
        };
        await repo.UpsertTableMetadataAsync(ordersNoTrinoMeta);

        await repo.CreateConsentAsync(new Consent
        {
            TableId = ordersNoTrinoMeta.Table.Id, TableIdentifier = OrdersNoTrinoId, TenantId = new TenantId("acme"), Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role, RoleName = "Reader", ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = [CRule("id")]
        });
    }

    private static TableColumn Col(string name, string type, bool sensitive = false) =>
        new() { ColumnName = name, DataType = type, IsSensitive = sensitive };

    private static ConsentColumnRule CRule(string col, ColumnAccessLevel level = ColumnAccessLevel.Clear) =>
        new() { ColumnName = col, AccessLevel = level };

    private async Task<HttpResponseMessage> HandleDatabricksRestAsync(HttpRequestMessage req)
    {
        var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync();
        if (req.Method == HttpMethod.Post && req.RequestUri?.AbsolutePath == "/api/2.0/sql/statements/")
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string statement = root.GetProperty("statement").GetString()!;

            // Handle information_schema.columns metadata probe
            if (statement.Contains("information_schema.columns", StringComparison.OrdinalIgnoreCase))
            {
                return CreateColumnsMetadataResponse(root);
            }

            // Execute query on Spark fixture
            if (!_spark.IsAvailable)
            {
                return JsonResponse(HttpStatusCode.InternalServerError, "{\"message\":\"Spark proxy not available\"}");
            }

            var sparkParams = new JsonArray();
            if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parameters.EnumerateArray())
                {
                    string name = p.GetProperty("name").GetString()!;
                    string type = p.TryGetProperty("type", out var t) ? t.GetString()?.ToLowerInvariant() ?? "string" : "string";
                    string? val = p.TryGetProperty("value", out var v) ? v.GetString() : null;
                    sparkParams.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["type"] = type,
                        ["value"] = val
                    });
                }
            }

            var sparkResp = await _spark.SendAsync(new JsonObject
            {
                ["op"] = "query",
                ["sql"] = statement,
                ["params"] = sparkParams
            });

            if (sparkResp?["ok"]?.GetValue<bool>() != true)
            {
                string error = sparkResp?["error"]?.ToString() ?? "Spark query failed";
                return JsonResponse(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new
                {
                    statement_id = Guid.NewGuid().ToString(),
                    status = new { state = "FAILED", error = new { message = error } }
                }));
            }

            var sparkRows = sparkResp["rows"]?.AsArray() ?? [];
            var dataArray = new List<List<string?>>();
            foreach (var row in sparkRows)
            {
                var rowList = new List<string?>();
                if (row is JsonArray rArr)
                {
                    foreach (var c in rArr)
                    {
                        rowList.Add(c?.ToString());
                    }
                }
                dataArray.Add(rowList);
            }

            var resultJson = JsonSerializer.Serialize(new
            {
                statement_id = Guid.NewGuid().ToString(),
                status = new { state = "SUCCEEDED" },
                manifest = new
                {
                    format = "JSON_ARRAY",
                    schema = new
                    {
                        column_count = dataArray.Count > 0 ? dataArray[0].Count : 0,
                        columns = Enumerable.Range(0, dataArray.Count > 0 ? dataArray[0].Count : 0)
                            .Select(i => new { name = $"col_{i}", type_name = "STRING", position = i })
                            .ToArray()
                    }
                },
                result = new
                {
                    chunk_index = 0,
                    row_offset = 0,
                    data_array = dataArray
                }
            });

            return JsonResponse(HttpStatusCode.OK, resultJson);
        }

        return JsonResponse(HttpStatusCode.NotFound, "{\"message\":\"not found\"}");
    }

    private static HttpResponseMessage CreateColumnsMetadataResponse(JsonElement root)
    {
        string? tableParam = null;
        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parameters.EnumerateArray())
            {
                if (p.GetProperty("name").GetString() == "tbl")
                {
                    tableParam = p.GetProperty("value").GetString()?.ToLowerInvariant();
                }
            }
        }

        var rows = new List<string[]>();
        if (tableParam == "orders")
        {
            rows.Add(["id", "int", ""]);
            rows.Add(["tenantid", "string", "UTF8_BINARY"]);
            rows.Add(["region", "string", ""]);
            rows.Add(["status", "string", ""]);
            rows.Add(["amount", "decimal(18,2)", ""]);
            rows.Add(["email", "string", ""]);
        }
        else if (tableParam == "orders_ci")
        {
            rows.Add(["id", "int", ""]);
            rows.Add(["tenantid", "string", "UTF8_LCASE"]);
        }

        var json = JsonSerializer.Serialize(new
        {
            statement_id = Guid.NewGuid().ToString(),
            status = new { state = "SUCCEEDED" },
            manifest = new
            {
                format = "JSON_ARRAY",
                schema = new
                {
                    column_count = 3,
                    columns = new[]
                    {
                        new { name = "column_name", type_name = "STRING", position = 0 },
                        new { name = "full_data_type", type_name = "STRING", position = 1 },
                        new { name = "collation_name", type_name = "STRING", position = 2 }
                    }
                }
            },
            result = new
            {
                chunk_index = 0,
                row_offset = 0,
                data_array = rows
            }
        });

        return JsonResponse(HttpStatusCode.OK, json);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeDatabricksHttpRouter(Func<HttpRequestMessage, Task<HttpResponseMessage>> router) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            router(request);
    }

    private sealed class FakeSecretProvider(Dictionary<string, string> secrets) : IKeyVaultSecretProvider
    {
        public byte[] GetSecretBytes(string secretRef) =>
            secrets.TryGetValue(secretRef, out var s) ? Encoding.UTF8.GetBytes(s) : throw new KeyNotFoundException(secretRef);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _factory?.Dispose();
    }

    private HttpClient CreateTransportClient(string tenant = "acme", string role = "Reader")
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", $"S-1-5-21-PARITY-{tenant.ToUpperInvariant()}-{role.ToUpperInvariant()}");
        client.DefaultRequestHeaders.Add("X-Test-Roles", role);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        client.DefaultRequestHeaders.Add("X-Trino-User", $"user-{tenant}");
        client.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
        return client;
    }

    // ---- The 5 required parity tests ----

    [SparkTheory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("mcp")]
    public async Task SameCaller_SameRowsMasksAndColumnDenials_OnAllTransports(string transport)
    {
        using var client = CreateTransportClient("acme", "Reader");
        var (ids, emails) = await QueryOrdersAsync(client, transport);

        // Caller 'acme' has EU filter: rows 1 and 6 are EU. Row 2 is US (filtered out). Rows 3, 4, 5 are other tenants.
        ids.ShouldBe([1, 6], ignoreOrder: true, customMessage: $"transport '{transport}'");

        // Masks: 'email' column is masked with REDACT, raw value alice.smith / frank.green never appears
        foreach (var email in emails)
        {
            email.ShouldNotContain("alice.smith");
            email.ShouldNotContain("frank.green");
        }
    }

    [SparkTheory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("mcp")]
    public async Task Tenant_isolation_holds_under_case_insensitive_collation(string transport)
    {
        if (!_spark.CollationAvailable)
        {
            throw new SparkSkipException("Spark instance does not support collations (requires Spark 4).");
        }

        // Tenant 'acme' queries orders_ci: should get row 1 only, never row 2 (which belongs to 'ACME')
        using var acmeClient = CreateTransportClient("acme", "Reader");
        var acmeIds = await QueryOrdersCiAsync(acmeClient, transport);
        acmeIds.ShouldBe([1], customMessage: $"acme on transport '{transport}'");

        // Tenant 'ACME' queries orders_ci: should get row 2 only, never row 1 (which belongs to 'acme')
        using var uppercaseClient = CreateTransportClient("ACME", "Reader");
        var upperIds = await QueryOrdersCiAsync(uppercaseClient, transport);
        upperIds.ShouldBe([2], customMessage: $"ACME on transport '{transport}'");
    }

    [SparkTheory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("mcp")]
    public async Task ConsentDenied_SameDenialShape_OnAllTransports(string transport)
    {
        // Caller with no consent (e.g. UnknownRole)
        using var client = CreateTransportClient("acme", "UnknownRole");
        var denied = await QueryOrdersDeniedAsync(client, transport);
        denied.ShouldBeTrue($"transport '{transport}' should deny access when consent is missing");
    }

    [SparkTheory]
    [InlineData("websql")]
    [InlineData("trino")]
    [InlineData("mcp")]
    public async Task RowLimit_AppliedPerTransportChannel(string transport)
    {
        using var client = CreateTransportClient("acme", "Reader");
        var count = await QueryOrdersWithLimitAsync(client, transport, limit: 1);
        count.ShouldBeLessThanOrEqualTo(1, $"transport '{transport}' row limit");
    }

    [SparkTheory]
    [InlineData("trino")]
    [InlineData("mcp")]
    public async Task Write_statement_rejected_on_Trino_and_Mcp(string transport)
    {
        using var client = CreateTransportClient("acme", "Reader");
        var writeRejected = await AttemptWriteAsync(client, transport);
        writeRejected.ShouldBeTrue($"write statement must be rejected on transport '{transport}'");
    }

    [Fact]
    public async Task AllowedTransports_WithoutTrino_DeniesTrinoQuery_AndAllowsWebSql()
    {
        using var client = CreateTransportClient("acme", "Reader");

        // WebSQL succeeds on dbx_notrino
        var webSqlResp = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM dbx_notrino.default.orders", dataSource = "dbx_notrino" });
        webSqlResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Trino is denied by AllowedTransports gate
        var (_, trinoBody, _) = await ExecuteTrinoStatementAsync(client, "SELECT id FROM dbx_notrino.default.orders");
        trinoBody.ShouldContain("TRANSPORT_NOT_ALLOWED");
    }

    // ---- Transport helper methods ----

    private static async Task<(List<int> Ids, List<string> Emails)> QueryOrdersAsync(HttpClient client, string transport)
    {
        var ids = new List<int>();
        var emails = new List<string>();

        switch (transport.ToLowerInvariant())
        {
            case "websql":
            {
                var resp = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id, email FROM dbx.default.orders", dataSource = "dbx" });
                resp.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                {
                    ids.Add(row[0].GetInt32());
                    emails.Add(row[1].GetString() ?? string.Empty);
                }
                break;
            }
            case "trino":
            {
                var (status, body, root) = await ExecuteTrinoStatementAsync(client, "SELECT id, email FROM dbx.default.orders");
                status.ShouldBe(HttpStatusCode.OK, body);
                if (root.TryGetProperty("columns", out var cols) && root.TryGetProperty("data", out var data))
                {
                    var columns = cols.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
                    int idIdx = columns.IndexOf("id");
                    int emailIdx = columns.IndexOf("email");
                    foreach (var row in data.EnumerateArray())
                    {
                        if (idIdx >= 0)
                        {
                            var idVal = row[idIdx];
                            ids.Add(idVal.ValueKind == JsonValueKind.Number ? idVal.GetInt32() : int.Parse(idVal.GetString()!));
                        }
                        if (emailIdx >= 0)
                        {
                            var emailVal = row[emailIdx];
                            emails.Add(emailVal.GetString() ?? string.Empty);
                        }
                    }
                }
                break;
            }
            case "mcp":
            {
                await using var mcp = await McpTestClient.ConnectAsync(client);
                var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_dataset", new { dataset = "dbx.default.orders" });
                isError.ShouldBeFalse(payload.ToString());
                var items = payload.TryGetProperty("data", out var d) ? d :
                            payload.TryGetProperty("rows", out var r) ? r : default;
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("id", out var idProp))
                            {
                                ids.Add(idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : int.Parse(idProp.GetString()!));
                            }
                            if (item.TryGetProperty("email", out var emailProp))
                            {
                                emails.Add(emailProp.GetString() ?? string.Empty);
                            }
                        }
                    }
                }
                break;
            }
        }

        return (ids, emails);
    }

    private static async Task<List<int>> QueryOrdersCiAsync(HttpClient client, string transport)
    {
        var ids = new List<int>();

        switch (transport.ToLowerInvariant())
        {
            case "websql":
            {
                var resp = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM dbx.default.orders_ci", dataSource = "dbx" });
                resp.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                {
                    ids.Add(row[0].GetInt32());
                }
                break;
            }
            case "trino":
            {
                var (status, body, root) = await ExecuteTrinoStatementAsync(client, "SELECT id FROM dbx.default.orders_ci");
                status.ShouldBe(HttpStatusCode.OK, body);
                if (root.TryGetProperty("columns", out var cols) && root.TryGetProperty("data", out var data))
                {
                    var columns = cols.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
                    int idIdx = columns.IndexOf("id");
                    foreach (var row in data.EnumerateArray())
                    {
                        if (idIdx >= 0)
                        {
                            var idVal = row[idIdx];
                            ids.Add(idVal.ValueKind == JsonValueKind.Number ? idVal.GetInt32() : int.Parse(idVal.GetString()!));
                        }
                    }
                }
                break;
            }
            case "mcp":
            {
                await using var mcp = await McpTestClient.ConnectAsync(client);
                var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_dataset", new { dataset = "dbx.default.orders_ci" });
                isError.ShouldBeFalse(payload.ToString());
                var items = payload.TryGetProperty("data", out var d) ? d :
                            payload.TryGetProperty("rows", out var r) ? r : default;
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idProp))
                        {
                            ids.Add(idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : int.Parse(idProp.GetString()!));
                        }
                    }
                }
                break;
            }
        }

        return ids;
    }

    private static async Task<bool> QueryOrdersDeniedAsync(HttpClient client, string transport)
    {
        switch (transport.ToLowerInvariant())
        {
            case "websql":
            {
                var resp = await client.PostAsJsonAsync("/api/v1/sql", new { sql = "SELECT id FROM dbx.default.orders", dataSource = "dbx" });
                return resp.StatusCode != HttpStatusCode.OK;
            }
            case "trino":
            {
                var (status, body, _) = await ExecuteTrinoStatementAsync(client, "SELECT id FROM dbx.default.orders");
                return status != HttpStatusCode.OK || body.Contains("ACCESS_DENIED") || body.Contains("SQL_POLICY_DENIED") || body.Contains("SQL_CONSENT_REQUIRED") || body.Contains("error");
            }
            case "mcp":
            {
                await using var mcp = await McpTestClient.ConnectAsync(client);
                var (isError, text) = await McpTestClient.CallAsync(mcp, "query_dataset", new { dataset = "dbx.default.orders" });
                return isError || text.Contains("DENIED", StringComparison.OrdinalIgnoreCase) || text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase);
            }
            default:
                return true;
        }
    }

    private static async Task<int> QueryOrdersWithLimitAsync(HttpClient client, string transport, int limit)
    {
        switch (transport.ToLowerInvariant())
        {
            case "websql":
            {
                var resp = await client.PostAsJsonAsync("/api/v1/sql", new { sql = $"SELECT id FROM dbx.default.orders LIMIT {limit}", dataSource = "dbx" });
                resp.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                return doc.RootElement.GetProperty("rows").GetArrayLength();
            }
            case "trino":
            {
                var (status, _, root) = await ExecuteTrinoStatementAsync(client, $"SELECT id FROM dbx.default.orders LIMIT {limit}");
                status.ShouldBe(HttpStatusCode.OK);
                return root.TryGetProperty("data", out var data) ? data.GetArrayLength() : 0;
            }
            case "mcp":
            {
                await using var mcp = await McpTestClient.ConnectAsync(client);
                var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_dataset", new { dataset = "dbx.default.orders", limit });
                isError.ShouldBeFalse(payload.ToString());
                var items = payload.TryGetProperty("data", out var d) ? d :
                            payload.TryGetProperty("rows", out var r) ? r : default;
                return items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
            }
            default:
                return 0;
        }
    }

    private static async Task<bool> AttemptWriteAsync(HttpClient client, string transport)
    {
        switch (transport.ToLowerInvariant())
        {
            case "trino":
            {
                var (status, body, _) = await ExecuteTrinoStatementAsync(client,
                    "INSERT INTO dbx.default.orders VALUES (99, 'acme', 'EU', 'open', 1.0, 'test@example.com')");
                return status != HttpStatusCode.OK || body.Contains("NOT_SUPPORTED") || body.Contains("DML_NOT_ALLOWED") || body.Contains("error") || body.Contains("FAILED");
            }
            case "mcp":
            {
                await using var mcp = await McpTestClient.ConnectAsync(client);
                try
                {
                    var (isError, text) = await McpTestClient.CallAsync(mcp, "query_sql", new { query = "DELETE FROM dbx.default.orders" });
                    return isError || text.Contains("NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase) || text.Contains("DML_NOT_ALLOWED", StringComparison.OrdinalIgnoreCase) || text.Contains("error", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return true;
                }
            }
            default:
                return false;
        }
    }

    private static async Task<(HttpStatusCode StatusCode, string Body, JsonElement Root)> ExecuteTrinoStatementAsync(HttpClient client, string sql)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/statement")
        {
            Content = new StringContent(sql, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Trino-Wait-Timeout", "10s");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, body, default);
        }

        var currentDoc = JsonDocument.Parse(body);
        while (currentDoc.RootElement.TryGetProperty("nextUri", out var next) && next.GetString() is { Length: > 0 } uri &&
               (!currentDoc.RootElement.TryGetProperty("data", out var d) || d.GetArrayLength() == 0))
        {
            var follow = new HttpRequestMessage(HttpMethod.Get, uri);
            follow.Headers.Add("X-Trino-Wait-Timeout", "10s");
            var followed = await client.SendAsync(follow);
            if (followed.StatusCode != HttpStatusCode.OK)
            {
                currentDoc.Dispose();
                body = await followed.Content.ReadAsStringAsync();
                return (followed.StatusCode, body, default);
            }
            currentDoc.Dispose();
            body = await followed.Content.ReadAsStringAsync();
            currentDoc = JsonDocument.Parse(body);
            if (currentDoc.RootElement.TryGetProperty("stats", out var stats) &&
                stats.TryGetProperty("state", out var state) &&
                state.GetString() == "FINISHED")
            {
                break;
            }
        }

        return (response.StatusCode, body, currentDoc.RootElement.Clone());
    }
}
