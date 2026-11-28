namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The MCP query tools answer the same payload and the same error code as the HTTP endpoint they mirror: the same request is sent to
/// MCP and to <c>POST /api/v1/sql</c> as the same caller and the parsed bodies are compared.
/// </summary>
public sealed partial class McpHttpContractEndToEndTests : IClassFixture<McpHttpContractEndToEndTests.HostFixture>
{
    private const string UserSid = "S-1-MCP-HTTP-USER";
    private const string Tenant = "tenant_http";
    private const int Cap = 100;
    private const string Sql = "SELECT id, label FROM default.main.mcp_http_rows ORDER BY id";
    private static readonly TableIdentifier Rows = new("default", "main", "mcp_http_rows");

    private readonly WebApplicationFactory<Program> _factory;

    public McpHttpContractEndToEndTests(HostFixture factory)
    {
        _factory = factory;
    }

    public sealed class HostFixture : IntegrationHost
    {
        private readonly string _dbPath;

        public HostFixture()
        {
            _dbPath = PostgresDataSource.Create("mcp_http", """
                CREATE TABLE mcp_http_rows (id INTEGER PRIMARY KEY, label TEXT NOT NULL, secret TEXT NOT NULL, denied_x TEXT NOT NULL);
                INSERT INTO mcp_http_rows SELECT g, 'label-' || g, 's3cret-' || g, 'd-' || g FROM generate_series(1, 520) AS g;
                """);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("McpHttpContractEndToEndTests"));
            builder.UseSetting("Gateway:DataSources:Connections:default:Provider", "PostgreSql");
            builder.UseSetting("Gateway:DataSources:Connections:default:DefaultSchema", "main");
            builder.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", _dbPath);
            builder.UseSetting("Gateway:DataSources:Connections:hidden:Provider", "PostgreSql");
            builder.UseSetting("Gateway:DataSources:Connections:hidden:DefaultSchema", "main");
            builder.UseSetting("Gateway:DataSources:Connections:hidden:ConnectionString", _dbPath);
            builder.UseSetting("Gateway:Mcp:Enabled", "true");
            builder.UseSetting("Gateway:Mcp:EndpointPath", "/mcp");
            builder.UseSetting("Gateway:Mcp:MaxResultRows", Cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:Mcp:MaxTokensPerCall", "128000");
            builder.UseSetting("Gateway:GraphQL:MaxAllowedComplexity", "10000");
            builder.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxCostPerQuery", "10000");
            builder.UseSetting("Gateway:RateLimiting:ClientTiers:TierLimits:Standard:MaxTokensCapacity", "100000");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:WebSql:AllowedDataSources:0", "default");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
        }
    }

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var columns = new (string Name, ColumnAccessLevel Level)[]
        {
            ("id", ColumnAccessLevel.Clear), ("label", ColumnAccessLevel.Clear), ("secret", ColumnAccessLevel.Mask), ("denied_x", ColumnAccessLevel.Deny)
        };
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = Rows.TableName, TableName = Rows.TableName, SchemaName = "main", SourceType = "PostgreSql", SourceName = "default", Description = "HTTP contract" },
            Identifier = Rows,
            Columns = columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = c.Name == "id" ? "integer" : "varchar", Description = c.Name }).ToList(),
        });

        var meta = await repo.GetTableMetadataAsync(Rows);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = meta!.Table.Id,
            TableIdentifier = Rows,
            TenantId = new TenantId(Tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules = columns.Select(c => new ConsentColumnRule { ColumnName = c.Name, AccessLevel = c.Level }).ToList(),
        });
    }

    private HttpClient Client(string sid = UserSid, string tenant = Tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", sid);
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        return client;
    }

    private static async Task<(bool IsError, JsonNode Body, JsonNode? Meta, string Text)> McpAsync(HttpClient client, object arguments)
    {
        await using var mcp = await McpTestClient.ConnectAsync(client);
        var result = await McpTestClient.CallResultAsync(mcp, "query_sql", arguments);
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, JsonNode.Parse(text)!, result.Meta is null ? null : JsonNode.Parse(result.Meta.ToJsonString()), text);
    }

    private static async Task<(int Status, JsonNode Body, string Text)> HttpAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/sql", body);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, JsonNode.Parse(text)!, text);
    }

    /// <summary>The next-token value differs per door (it binds the transport), so only its presence is compared.</summary>
    private static (JsonNode Body, bool HasNext) WithoutNext(JsonNode body)
    {
        var copy = JsonNode.Parse(body.ToJsonString())!.AsObject();
        var has = copy.Remove("next");
        return (copy, has);
    }

    private static void ShouldEqualHttp(JsonNode mcp, JsonNode http)
    {
        var (m, mNext) = WithoutNext(mcp);
        var (h, hNext) = WithoutNext(http);
        mNext.ShouldBe(hNext);
        JsonNode.DeepEquals(m, h).ShouldBeTrue($"MCP {m.ToJsonString()} vs HTTP {h.ToJsonString()}");
    }

    private static int[] Ids(JsonNode body) => body["rows"]!.AsArray().Select(r => r!["id"]!.GetValue<int>()).ToArray();

    [Fact]
    public async Task QuerySql_PageBelowCap_BodyEqualsWebSql()
    {
        await SeedAsync();
        var client = Client();

        var mcp = await McpAsync(client, new { sql = Sql, page = new { size = 7 } });
        var http = await HttpAsync(client, new { sql = Sql, page = new { size = 7 } });

        mcp.IsError.ShouldBeFalse(mcp.Text);
        http.Status.ShouldBe(200, http.Text);
        ShouldEqualHttp(mcp.Body, http.Body);
        mcp.Meta!["pageSizeCapped"].ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_PageAboveCap_CappedNextContinues_EqualsWebSqlPageTwo()
    {
        await SeedAsync();
        var client = Client();

        var first = await McpAsync(client, new { sql = Sql, page = new { size = 250 } });
        first.IsError.ShouldBeFalse(first.Text);
        Ids(first.Body).Length.ShouldBe(Cap);
        first.Meta!["pageSizeCapped"]!.GetValue<bool>().ShouldBeTrue();
        first.Body["next"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        first.Body.AsObject().ContainsKey("rowCap").ShouldBeFalse();

        var second = await McpAsync(client, new { sql = Sql, page = new { size = 250 }, next = first.Body["next"]!.GetValue<string>() });
        var httpFirst = await HttpAsync(client, new { sql = Sql, page = new { size = Cap } });
        var httpSecond = await HttpAsync(client, new { sql = Sql, page = new { size = Cap }, next = httpFirst.Body["next"]!.GetValue<string>() });

        second.IsError.ShouldBeFalse(second.Text);
        Ids(second.Body).ShouldBe(Enumerable.Range(Cap + 1, Cap).ToArray());
        ShouldEqualHttp(second.Body, httpSecond.Body);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_PageAboveCap_NeverExceedsCap_NextContinues()
    {
        await SeedAsync();
        var client = Client();
        var seen = 0;
        string? next = null;

        for (var page = 0; page < 8; page++)
        {
            var result = await McpAsync(client, next is null ? new { sql = Sql, page = new { size = 5000 } } : (object)new { sql = Sql, page = new { size = 5000 }, next });
            result.IsError.ShouldBeFalse(result.Text);
            Ids(result.Body).Length.ShouldBeLessThanOrEqualTo(Cap);
            seen += Ids(result.Body).Length;
            next = result.Body["next"]?.GetValue<string>();
            if (next is null)
            {
                break;
            }
        }

        seen.ShouldBe(520);
    }

    [Fact]
    public async Task QuerySql_DataSourceAndNext_RoundTrip()
    {
        await SeedAsync();
        var client = Client();

        var first = await McpAsync(client, new { sql = Sql, dataSource = "default", page = new { size = 10 } });
        var second = await McpAsync(client, new { sql = Sql, dataSource = "default", page = new { size = 10 }, next = first.Body["next"]!.GetValue<string>() });

        first.IsError.ShouldBeFalse(first.Text);
        second.IsError.ShouldBeFalse(second.Text);
        Ids(second.Body).ShouldBe(Enumerable.Range(11, 10).ToArray());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_SyntaxDeniedLimitTokenErrors_CodesAndFieldsEqualWebSql()
    {
        await SeedAsync();
        var client = Client();

        foreach (var sql in new[] { "SELECT FROM WHERE ((", "SELECT * FROM default.main.mcp_http_ghost" })
        {
            var mcp = await McpAsync(client, new { sql });
            var http = await HttpAsync(client, new { sql });
            mcp.IsError.ShouldBeTrue(mcp.Text);
            mcp.Body["error"]!["code"]!.GetValue<string>().ShouldBe(http.Body["code"]!.GetValue<string>(), sql);
            foreach (var field in new[] { "line", "column", "limit", "estimatedCost" })
            {
                (mcp.Body["error"]![field]?.ToJsonString()).ShouldBe(http.Body[field]?.ToJsonString(), $"{field} of {sql}");
            }
        }

        var badToken = await McpAsync(client, new { sql = Sql, page = new { size = 5 }, next = "tampered-token" });
        var httpToken = await HttpAsync(client, new { sql = Sql, page = new { size = 5 }, next = "tampered-token" });
        badToken.Body["error"]!["code"]!.GetValue<string>().ShouldBe(httpToken.Body["code"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_OldArgumentNames_InvalidParams()
    {
        await SeedAsync();
        var client = Client();

        foreach (var arguments in new object[]
        {
            new { query = Sql },
            new { sql = Sql, limit = 5 },
            new { sql = Sql, cursor = "abc" },
        })
        {
            var result = await McpAsync(client, arguments);

            result.IsError.ShouldBeTrue(result.Text);
            result.Body["error"]!["code"]!.GetValue<string>().ShouldBe("INVALID_PARAMS");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_OldNames_InvalidParams() => await QuerySql_OldArgumentNames_InvalidParams();

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_MaskedColumn_ColumnsMaskedAndMetaIsMasked()
    {
        await SeedAsync();
        var client = Client();
        const string maskedSql = "SELECT id, secret FROM default.main.mcp_http_rows ORDER BY id";

        var mcp = await McpAsync(client, new { sql = maskedSql, page = new { size = 3 } });
        var http = await HttpAsync(client, new { sql = maskedSql, page = new { size = 3 } });

        mcp.IsError.ShouldBeFalse(mcp.Text);
        mcp.Meta!["isMasked"]!.GetValue<bool>().ShouldBeTrue();
        mcp.Body["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "secret")!["masked"]!.GetValue<bool>().ShouldBeTrue();
        mcp.Text.ShouldNotContain("s3cret-");
        ShouldEqualHttp(mcp.Body, http.Body);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_MaskedColumn_ColumnsMaskedEqualHttp_AndMetaIsMasked() => await QuerySql_MaskedColumn_ColumnsMaskedAndMetaIsMasked();

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_DataSourceUnknownVsDisallowed_ByteIdentical()
    {
        await SeedAsync();
        var client = Client();

        var unknown = await McpAsync(client, new { sql = Sql, dataSource = "nowhere" });
        var disallowed = await McpAsync(client, new { sql = Sql, dataSource = "hidden" });
        var http = await HttpAsync(client, new { sql = Sql, dataSource = "hidden" });

        unknown.IsError.ShouldBeTrue(unknown.Text);
        disallowed.Text.ShouldBe(unknown.Text);
        unknown.Body["error"]!["code"]!.GetValue<string>().ShouldBe(http.Body["code"]!.GetValue<string>());
        unknown.Text.ShouldNotContain("hidden");
        unknown.Text.ShouldNotContain("nowhere");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task QuerySql_NextFromWebSql_RejectedOverMcp_AndViceVersa()
    {
        await SeedAsync();
        var client = Client();

        var http = await HttpAsync(client, new { sql = Sql, page = new { size = 5 } });
        var mcp = await McpAsync(client, new { sql = Sql, page = new { size = 5 } });

        var overMcp = await McpAsync(client, new { sql = Sql, page = new { size = 5 }, next = http.Body["next"]!.GetValue<string>() });
        var overHttp = await HttpAsync(client, new { sql = Sql, page = new { size = 5 }, next = mcp.Body["next"]!.GetValue<string>() });

        overMcp.IsError.ShouldBeTrue(overMcp.Text);
        overMcp.Body["error"]!["code"]!.GetValue<string>().ShouldBe("SQL_PAGING_TOKEN_INVALID");
        overHttp.Status.ShouldBe(400, overHttp.Text);
        overHttp.Body["code"]!.GetValue<string>().ShouldBe("SQL_PAGING_TOKEN_INVALID");
    }
}
