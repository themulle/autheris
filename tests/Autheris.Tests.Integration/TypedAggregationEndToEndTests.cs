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
/// #112 WP-6 / T-6.1: HTTP -> GraphQL -> typed <c>T_aggregate</c> / <c>queryMetrics</c> -> governed planner path -> real SQLite file.
/// The table holds 100,001 rows (above the in-memory aggregation cap), so an exact count proves the planner path; the group sizes
/// are chosen to exercise k-anonymity (k = 5) and the honest <c>truncated</c> flag; the manifest import survives a host restart
/// (a second host over the same governance database file).
/// </summary>
public sealed class TypedAggregationEndToEndTests : IClassFixture<TypedAggregationEndToEndTests.HostFixture>
{
    private const string Field = "default_main_agg_orders_aggregate";

    private readonly HostFixture _fixture;

    public TypedAggregationEndToEndTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        private readonly string _workDir;
        private readonly string _dataPath;
        private readonly string _govPath;

        public HostFixture()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "autheris-agg-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
            _govPath = Path.Combine(_workDir, "gov.db");
            _dataPath = Seed();

            using var seeding = NewHost();
            RegisterCatalogAsync(seeding).GetAwaiter().GetResult();
        }

        public IntegrationHost NewHost() => new(Settings(_govPath, _dataPath));

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(Settings(_govPath, _dataPath));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            NpgsqlConnection.ClearAllPools();
            try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
        }

        private static Dictionary<string, string?> Settings(string govPath, string dataPath) => new()
        {
            ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
            ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "100000",
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.CreateStable(govPath),
            ["Gateway:Insecure:danger_bypass_consent_checks"] = "true",
            // The count of suppressed groups is not exposed by default (privacy); this test asserts it.
            ["Analytics:Privacy:ExposeSuppressedGroupsCount"] = "true",
            ["Gateway:DataSources:Connections:default:Provider"] = "PostgreSql",
            ["Gateway:DataSources:Connections:default:DefaultSchema"] = "main",
            ["Gateway:DataSources:Connections:default:ConnectionString"] = dataPath,
        };
    }

    // Groups: A 99,991 rows, B 5, C 4, D 1 = 100,001 rows.
    private static string Seed() => PostgresDataSource.Create("aggorders", """
        CREATE TABLE agg_orders (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount INTEGER NOT NULL);
        INSERT INTO agg_orders (id, region, amount)
        SELECT row_number() OVER (), r, 2 FROM (
            SELECT 'A' AS r FROM generate_series(1, 99981)
            UNION ALL SELECT 'B' FROM generate_series(1, 10)
            UNION ALL SELECT 'E' FROM generate_series(1, 5)
            UNION ALL SELECT 'C' FROM generate_series(1, 4)
            UNION ALL SELECT 'D' FROM generate_series(1, 1)) AS t;
        """);

    private WebApplicationFactory<Program> NewHost() => _fixture.NewHost();

    private Task<WebApplicationFactory<Program>> StartHostAsync() => Task.FromResult<WebApplicationFactory<Program>>(_fixture);

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "agg_orders", TableName = "agg_orders", SchemaName = "main", SourceType = "PostgreSql", SourceName = "default" },
            Identifier = new TableIdentifier("default", "main", "agg_orders"),
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "integer" },
                new() { ColumnName = "region", DataType = "varchar" },
                new() { ColumnName = "amount", DataType = "integer" }
            }
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string roles = "Reader", string tenant = "tenant_e2e")
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-E2E-AGG");
        c.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        c.DefaultRequestHeaders.Add("X-Test-Tenant", tenant);
        c.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        return c;
    }

    private static async Task<JsonDocument> GraphQlAsync(HttpClient client, string query)
    {
        var body = JsonSerializer.Serialize(new { query });
        var response = await client.PostAsync("/graphql", new StringContent(body, Encoding.UTF8, "application/json"));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Data(JsonDocument doc, string field)
    {
        doc.RootElement.TryGetProperty("errors", out var errors).ShouldBeFalse(errors.ToString());
        return doc.RootElement.GetProperty("data").GetProperty(field);
    }

    [Fact]
    public async Task Aggregate_OverMoreRowsThanTheInMemoryCap_ReturnsTheExactCount()
    {
        var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host), $"{{ {Field}(aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count truncated groups {{ aggregates {{ alias kind intValue }} }} }} }}");

        var data = Data(doc, Field);
        data.GetProperty("count").GetInt64().ShouldBe(100_001);
        data.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        var value = data.GetProperty("groups")[0].GetProperty("aggregates")[0];
        value.GetProperty("kind").GetString().ShouldBe("INT");
        value.GetProperty("intValue").GetInt64().ShouldBe(100_001);
    }

    [Fact]
    public async Task Aggregate_SmallGroups_AreSuppressed_AndTheCountsCoverVisibleGroupsOnly()
    {
        var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host),
            $"{{ {Field}(groupBy: [region], aggregates: [{{ fn: SUM, column: amount, alias: \"total\" }}], orderBy: [{{ column: region }}]) " +
            "{ count suppressed suppressedGroupsCount minimumGroupSize groups { rowCount key { region } aggregates { alias kind intValue } } } }");

        var data = Data(doc, Field);
        var groups = data.GetProperty("groups").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("key").GetProperty("region").GetString()).ShouldBe(["A", "B"]);
        groups.Select(g => g.GetProperty("rowCount").GetInt64()).ShouldBe([99_981, 10]);
        groups[0].GetProperty("aggregates")[0].GetProperty("intValue").GetInt64().ShouldBe(2 * 99_981);
        data.GetProperty("suppressed").GetBoolean().ShouldBeTrue();
        data.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(3);
        data.GetProperty("minimumGroupSize").GetInt32().ShouldBe(5);
        data.GetProperty("count").GetInt64().ShouldBe(99_991);
        data.ToString().ShouldNotContain("suppressedRowsCount");
    }

    [Fact]
    public async Task Aggregate_LimitBelowTheGroupCount_SetsTruncated()
    {
        var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host),
            $"{{ {Field}(groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: region }}], limit: 1) {{ truncated groups {{ key {{ region }} }} }} }}");

        var data = Data(doc, Field);
        data.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("groups").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Aggregate_ColumnOutsideTheGeneratedEnum_IsRejectedByValidation()
    {
        var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host), $"{{ {Field}(groupBy: [nope], aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        doc.RootElement.TryGetProperty("errors", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("data", out var data).ShouldBeFalse(doc.RootElement.ToString() + data);
    }

    private const string Manifest = """
        { "version": "v9",
          "semantic_models": [ { "name": "orders", "node_relation": { "relation_name": "default.main.agg_orders" },
            "entities": [ { "name": "id", "type": "primary" } ],
            "measures": [ { "name": "order_total", "agg": "sum", "expr": "amount" } ],
            "dimensions": [ { "name": "region", "type": "categorical" } ] } ],
          "metrics": [ { "name": "revenue", "type": "simple", "label": "Revenue", "dimensions": ["region"],
                         "type_params": { "measure": "order_total" } } ] }
        """;

    [Fact]
    public async Task Manifest_ImportedBeforeARestart_AnswersQueryMetricsAfterTheRestart()
    {
        var first = await StartHostAsync();
        var import = await Client(first, roles: "GovernanceAdmin,ClusterAdmin,DbtAdmin")
            .PostAsync("/api/extensions/dbt/semantic-manifest", new StringContent(Manifest, Encoding.UTF8, "application/json"));
        import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());

        using var restarted = NewHost();
        using var doc = await GraphQlAsync(Client(restarted),
            "{ queryMetrics(metrics: [\"revenue\"], groupBy: [\"region\"], orderBy: [{ dimension: \"region\" }]) " +
            "{ manifestVersion groups { dimensions { name kind stringValue } values { alias kind intValue decimalValue } } metricDefinitions { name label } } }");

        var data = Data(doc, "queryMetrics");
        data.GetProperty("manifestVersion").GetString().ShouldBe("v9");
        var group = data.GetProperty("groups")[0];
        group.GetProperty("dimensions")[0].GetProperty("stringValue").GetString().ShouldBe("A");
        var value = group.GetProperty("values")[0];
        value.GetProperty("alias").GetString().ShouldBe("revenue");
        data.GetProperty("metricDefinitions")[0].GetProperty("label").GetString().ShouldBe("Revenue");
    }

    [Fact]
    public async Task Metric_OfAnotherTenant_IsUnknown()
    {
        var host = await StartHostAsync();
        (await Client(host, roles: "GovernanceAdmin,ClusterAdmin,DbtAdmin", tenant: "tenant_a")
            .PostAsync("/api/extensions/dbt/semantic-manifest", new StringContent(Manifest, Encoding.UTF8, "application/json")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = await GraphQlAsync(Client(host, tenant: "tenant_b"), "{ queryMetrics(metrics: [\"revenue\"]) { count } }");

        doc.RootElement.ToString().ShouldContain("UNKNOWN_METRIC");
    }
}
