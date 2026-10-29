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
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// #112 WP-6 / T-6.1: HTTP -> GraphQL -> typed <c>T_aggregate</c> / <c>queryMetrics</c> -> governed planner path -> real SQLite file.
/// The table holds 100,001 rows (above the in-memory aggregation cap), so an exact count proves the planner path; the group sizes
/// are chosen to exercise k-anonymity (k = 5) and the honest <c>truncated</c> flag; the manifest import survives a host restart
/// (a second host over the same governance database file).
/// </summary>
public sealed class TypedAggregationEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Field = "default_main_agg_orders_aggregate";

    private readonly string _workDir;
    private readonly string _dataPath;
    private readonly string _govPath;
    private readonly WebApplicationFactory<Program> _baseFactory;

    public TypedAggregationEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
        _workDir = Path.Combine(Path.GetTempPath(), "autheris-agg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
        _dataPath = Path.Combine(_workDir, "data.db");
        _govPath = Path.Combine(_workDir, "gov.db");
        Seed(_dataPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workDir, true); } catch { /* best effort */ }
    }

    // Groups: A 99,991 rows, B 5, C 4, D 1 = 100,001 rows.
    private static void Seed(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var create = conn.CreateCommand();
        create.CommandText = "CREATE TABLE agg_orders (id INTEGER PRIMARY KEY, region TEXT NOT NULL, amount INTEGER NOT NULL);";
        create.ExecuteNonQuery();
        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO agg_orders (region, amount) VALUES ($r, 2);";
        var region = insert.CreateParameter();
        region.ParameterName = "$r";
        insert.Parameters.Add(region);
        foreach (var (name, count) in new[] { ("A", 99_991), ("B", 5), ("C", 4), ("D", 1) })
        {
            region.Value = name;
            for (var i = 0; i < count; i++)
            {
                insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private WebApplicationFactory<Program> NewHost() => _baseFactory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
        b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
        b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
        b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={_govPath}");
        b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
        b.UseSetting("Gateway:Rebac:Enabled", "false");
        b.UseSetting("Gateway:Tenancy:Mode", "Multi");
        b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
        b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataPath}");
    });

    /// <summary>
    /// Registers the table in the governance database file with a first host and returns a second host over the same file: the GraphQL
    /// schema is built from the catalog at start-up, so the table has to be there before the host that serves the test starts.
    /// </summary>
    private async Task<WebApplicationFactory<Program>> StartHostAsync()
    {
        using (var seeding = NewHost())
        {
            await RegisterCatalogAsync(seeding);
        }

        return NewHost();
    }

    private static async Task RegisterCatalogAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "agg_orders", TableName = "agg_orders", SchemaName = "main", SourceType = "Sqlite", SourceName = "default" },
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
        using var host = await StartHostAsync();

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
        using var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host),
            $"{{ {Field}(groupBy: [region], aggregates: [{{ fn: SUM, column: amount, alias: \"total\" }}], orderBy: [{{ column: region }}]) " +
            "{ count suppressed suppressedGroupsCount minimumGroupSize groups { rowCount key { region } aggregates { alias kind intValue } } } }");

        var data = Data(doc, Field);
        var groups = data.GetProperty("groups").EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("key").GetProperty("region").GetString()).ShouldBe(["A", "B"]);
        groups.Select(g => g.GetProperty("rowCount").GetInt64()).ShouldBe([99_991, 5]);
        groups[0].GetProperty("aggregates")[0].GetProperty("intValue").GetInt64().ShouldBe(2 * 99_991);
        data.GetProperty("suppressed").GetBoolean().ShouldBeTrue();
        data.GetProperty("suppressedGroupsCount").GetInt32().ShouldBe(2);
        data.GetProperty("minimumGroupSize").GetInt32().ShouldBe(5);
        data.GetProperty("count").GetInt64().ShouldBe(99_996);
        data.ToString().ShouldNotContain("suppressedRowsCount");
    }

    [Fact]
    public async Task Aggregate_LimitBelowTheGroupCount_SetsTruncated()
    {
        using var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host),
            $"{{ {Field}(groupBy: [region], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: region }}], limit: 1) {{ truncated groups {{ key {{ region }} }} }} }}");

        var data = Data(doc, Field);
        data.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("groups").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Aggregate_ColumnOutsideTheGeneratedEnum_IsRejectedByValidation()
    {
        using var host = await StartHostAsync();

        using var doc = await GraphQlAsync(Client(host), $"{{ {Field}(groupBy: [nope], aggregates: [{{ fn: COUNT, alias: \"n\" }}]) {{ count }} }}");

        doc.RootElement.TryGetProperty("errors", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("data", out var data).ShouldBeFalse(doc.RootElement.ToString() + data);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-06")]
    public async Task Aggregate_MinUnderNoise_ReturnsPrivacyRestrictedCode()
    {
        using (var seeding = NewHost())
        {
            await RegisterCatalogAsync(seeding);
        }

        using var host = NewHost().WithWebHostBuilder(b =>
        {
            b.UseSetting("Analytics:Privacy:Noise:Enabled", "true");
            b.UseSetting("Analytics:Privacy:Noise:Epsilon", "1");
            b.UseSetting("Analytics:Privacy:Noise:DailyBudgetEpsilon", "100");
            b.UseSetting("Analytics:Privacy:Noise:MeasureBounds:amount:Min", "0");
            b.UseSetting("Analytics:Privacy:Noise:MeasureBounds:amount:Max", "10");
        });

        using var doc = await GraphQlAsync(Client(host), $"{{ {Field}(aggregates: [{{ fn: MIN, column: amount, alias: \"sentinelalias\" }}]) {{ count }} }}");

        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        errors.Select(e => e.GetProperty("extensions").GetProperty("code").GetString()).ShouldBe(["AGGREGATION_PRIVACY_RESTRICTED"]);
        errors[0].GetProperty("message").GetString().ShouldBe("The requested aggregate is not available under the privacy policy.");
        doc.RootElement.ToString().ShouldNotContain("sentinelalias");
        doc.RootElement.ToString().ShouldNotContain("INTERNAL_SERVER_ERROR");
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
        using (var first = await StartHostAsync())
        {
            var import = await Client(first, roles: "GovernanceAdmin,ClusterAdmin,DbtAdmin")
                .PostAsync("/api/extensions/dbt/semantic-manifest", new StringContent(Manifest, Encoding.UTF8, "application/json"));
            import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync());
        }

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
        using var host = await StartHostAsync();
        (await Client(host, roles: "GovernanceAdmin,ClusterAdmin,DbtAdmin", tenant: "tenant_a")
            .PostAsync("/api/extensions/dbt/semantic-manifest", new StringContent(Manifest, Encoding.UTF8, "application/json")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = await GraphQlAsync(Client(host, tenant: "tenant_b"), "{ queryMetrics(metrics: [\"revenue\"]) { count } }");

        doc.RootElement.ToString().ShouldContain("UNKNOWN_METRIC");
    }
}
