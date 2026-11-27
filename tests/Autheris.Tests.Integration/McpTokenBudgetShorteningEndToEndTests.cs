namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// An over-budget MCP data result is cut to whole rows and continues without gaps or duplicates (query_sql cursor, query_dataset
/// offset); a single row larger than the budget is a TOKEN_BUDGET_EXCEEDED error.
/// </summary>
public sealed class McpTokenBudgetShorteningEndToEndTests : IClassFixture<McpTokenBudgetShorteningEndToEndTests.HostFixture>
{
    private const string UserSid = "S-1-MCP-BUDGET-USER";
    private const string Tenant = "tenant_budget";
    private const int RowCount = 12;
    private static readonly TableIdentifier Rows = new("default", "main", "mcp_budget_rows");
    private static readonly TableIdentifier Wide = new("default", "main", "mcp_budget_wide");

    private readonly WebApplicationFactory<Program> _factory;

    public McpTokenBudgetShorteningEndToEndTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        private readonly string _dbPath;

        public HostFixture()
        {
            _dbPath = PostgresDataSource.Create("mcp_budget", """
                CREATE TABLE mcp_budget_rows (id INTEGER PRIMARY KEY, note TEXT NOT NULL, secret TEXT NOT NULL);
                INSERT INTO mcp_budget_rows SELECT g, repeat('n', 150) || g, 's3cret-' || g FROM generate_series(1, 12) AS g;
                CREATE TABLE mcp_budget_wide (id INTEGER PRIMARY KEY, blob TEXT NOT NULL);
                INSERT INTO mcp_budget_wide SELECT 1, repeat('w', 2000);
                """);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("McpTokenBudgetShorteningEndToEndTests"));
            builder.UseSetting("Gateway:DataSources:Connections:default:Provider", "PostgreSql");
            builder.UseSetting("Gateway:DataSources:Connections:default:DefaultSchema", "main");
            builder.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", _dbPath);
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:Mcp:MaxTokensPerCall", "256");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private async Task SeedAsync(TableIdentifier id, params (string Name, ColumnAccessLevel Level)[] columns)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = "main", SourceType = "PostgreSql", SourceName = "default", Description = "Budget" },
            Identifier = id,
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Name == "id" ? "integer" : "varchar", Description = c.Name }).ToList(),
        });

        var meta = await repo.GetTableMetadataAsync(id);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = id,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = columns.Select(c => new ConsentColumnRule { ColumnName = c.Name, AccessLevel = c.Level }).ToList(),
        });
    }

    private Task SeedRowsAsync() => SeedAsync(Rows, ("id", ColumnAccessLevel.Clear), ("note", ColumnAccessLevel.Clear), ("secret", ColumnAccessLevel.Mask));

    private Task SeedWideAsync() => SeedAsync(Wide, ("id", ColumnAccessLevel.Clear), ("blob", ColumnAccessLevel.Clear));

    private HttpClient Client(string sid = UserSid, string tenant = Tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        return client;
    }

    private static async Task<(bool IsError, JsonElement Payload, JsonElement? Meta)> CallAsync(HttpClient client, string tool, object arguments)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var result = await McpTestClient.CallResultAsync(mcp, tool, arguments);
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        JsonElement? meta = result.Meta is null ? null : JsonDocument.Parse(result.Meta.ToJsonString()).RootElement.Clone();
        return (result.IsError == true, JsonDocument.Parse(text).RootElement.Clone(), meta);
    }

    private static int[] Ids(JsonElement rows, string idProperty = "id") =>
        rows.EnumerateArray().Select(r => r.GetProperty(idProperty).GetInt32()).ToArray();

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_OverBudget_CursorPagesAllRowsWithoutGapOrDuplicate()
    {
        await SeedRowsAsync();
        var client = Client();
        const string sql = "SELECT id, note FROM default.main.mcp_budget_rows ORDER BY id";
        var seen = new List<int>();
        string? cursor = null;

        for (var page = 0; page < RowCount + 1; page++)
        {
            var (isError, payload, meta) = await CallAsync(client, "query_sql", cursor is null ? new { query = sql } : (object)new { query = sql, cursor });
            isError.ShouldBeFalse(payload.ToString());
            seen.AddRange(Ids(payload.GetProperty("rows")));
            if (page == 0)
            {
                meta!.Value.GetProperty("truncated").GetBoolean().ShouldBeTrue();
                meta.Value.GetProperty("truncationReason").GetString().ShouldBe("TOKEN_BUDGET");
                meta.Value.GetProperty("returnedRows").GetInt32().ShouldBeLessThan(RowCount);
                payload.GetProperty("rowCount").GetInt32().ShouldBe(meta.Value.GetProperty("returnedRows").GetInt32());
            }

            if (!payload.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
            {
                break;
            }

            cursor = next.GetString();
        }

        seen.ShouldBe(Enumerable.Range(1, RowCount).ToArray());
    }

    [Fact]
    public async Task McpQueryDataset_OverBudget_OffsetPlusCountContinues()
    {
        await SeedRowsAsync();
        var client = Client();
        var seen = new List<int>();
        var offset = 0;

        for (var page = 0; page < RowCount + 1; page++)
        {
            var (isError, payload, _) = await CallAsync(client, "query_dataset", new { dataset = "default.main.mcp_budget_rows", select = new[] { "id", "note" }, orderBy = "id", offset });
            isError.ShouldBeFalse(payload.ToString());
            var ids = Ids(payload.GetProperty("data"));
            seen.AddRange(ids);
            payload.GetProperty("count").GetInt32().ShouldBe(ids.Length);
            if (!payload.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            offset += payload.GetProperty("count").GetInt32();
        }

        seen.ShouldBe(Enumerable.Range(1, RowCount).ToArray());
    }

    [Fact]
    public async Task McpSampleRows_OverBudget_WholeRowsMetaTruncated()
    {
        await SeedRowsAsync();

        var (isError, payload, meta) = await CallAsync(Client(), "sample_rows", new { dataset = "default.main.mcp_budget_rows", count = RowCount });

        isError.ShouldBeFalse(payload.ToString());
        meta!.Value.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        meta.Value.GetProperty("returnedRows").GetInt32().ShouldBe(payload.GetProperty("rows").GetArrayLength());
        payload.GetProperty("rows").GetArrayLength().ShouldBeInRange(1, RowCount - 1);
        payload.TryGetProperty("next", out _).ShouldBeFalse();
        payload.TryGetProperty("cursor", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_SingleRowOverBudget_IsErrorTokenBudgetExceeded()
    {
        await SeedWideAsync();

        var (isError, payload, meta) = await CallAsync(Client(), "query_sql", new { query = "SELECT id, blob FROM default.main.mcp_budget_wide ORDER BY id" });

        isError.ShouldBeTrue();
        payload.GetProperty("error").GetProperty("code").GetString().ShouldBe("TOKEN_BUDGET_EXCEEDED");
        payload.ToString().ShouldNotContain("wwww");
        meta?.TryGetProperty("returnedRows", out _).ShouldNotBe(true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_Shortened_MaskedColumnStaysMasked_MetaIsMaskedTrue()
    {
        await SeedRowsAsync();

        var (isError, payload, meta) = await CallAsync(Client(), "query_sql", new { query = "SELECT id, note, secret FROM default.main.mcp_budget_rows ORDER BY id" });

        isError.ShouldBeFalse(payload.ToString());
        meta!.Value.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        meta.Value.GetProperty("isMasked").GetBoolean().ShouldBeTrue();
        payload.ToString().ShouldNotContain("s3cret-");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_ShortenedCursor_OtherUserOrTenant_IsInvalidParams()
    {
        await SeedRowsAsync();
        const string sql = "SELECT id, note FROM default.main.mcp_budget_rows ORDER BY id";
        var (_, first, _) = await CallAsync(Client(), "query_sql", new { query = sql });
        var cursor = first.GetProperty("next").GetString();

        var (otherUserError, otherUser, _) = await CallAsync(Client("S-1-MCP-BUDGET-OTHER"), "query_sql", new { query = sql, cursor });
        var (otherTenantError, otherTenant, _) = await CallAsync(Client(tenant: "tenant_other"), "query_sql", new { query = sql, cursor });

        otherUserError.ShouldBeTrue();
        otherTenantError.ShouldBeTrue();
        otherUser.ToString().ShouldNotContain("nnnn");
        otherTenant.ToString().ShouldNotContain("nnnn");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_ShortenedCursor_Tampered_IsInvalidParams()
    {
        await SeedRowsAsync();
        const string sql = "SELECT id, note FROM default.main.mcp_budget_rows ORDER BY id";
        var (_, first, _) = await CallAsync(Client(), "query_sql", new { query = sql });
        var cursor = first.GetProperty("next").GetString()!;
        var index = cursor.IndexOf('.', StringComparison.Ordinal) + 8;
        var tampered = cursor[..index] + (cursor[index] == 'A' ? 'B' : 'A') + cursor[(index + 1)..];

        var (isError, payload, _) = await CallAsync(Client(), "query_sql", new { query = sql, cursor = tampered });

        isError.ShouldBeTrue();
        payload.GetProperty("error").GetProperty("code").GetString().ShouldBe("INVALID_PARAMS");
    }
}
