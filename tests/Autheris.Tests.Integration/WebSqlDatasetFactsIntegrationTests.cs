using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>Opt-in <c>datasetFacts</c> of WebSQL and <c>query_sql</c> over the real host: consent row filter on <c>default_main_air1</c>.</summary>
public sealed class WebSqlDatasetFactsIntegrationTests : IClassFixture<RowFilterChannelParityTests.Fixture>
{
    private const string Sql = "SELECT a.id FROM default.main.air1 a JOIN default.main.client c ON c.client_id = a.client_id WHERE c.client_id <> @marker";
    private const string Marker = "marker-value-4711";

    private readonly RowFilterChannelParityTests.Fixture _fixture;

    public WebSqlDatasetFactsIntegrationTests(RowFilterChannelParityTests.Fixture fixture) => _fixture = fixture;

    private async Task<JsonDocument> WebSqlAsync(bool? datasetFacts)
    {
        var body = datasetFacts is null
            ? (object)new { sql = Sql, parameters = new { marker = -1 } }
            : new { sql = Sql, parameters = new { marker = -1 }, datasetFacts };
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    [Fact]
    public async Task WebSql_WithFlag_ReportsRowFilterPerTableAsFixedText()
    {
        using var doc = await WebSqlAsync(true);

        var facts = doc.RootElement.GetProperty("datasetFacts");
        facts.GetProperty("truncated").GetBoolean().ShouldBe(doc.RootElement.GetProperty("truncated").GetBoolean());
        var tables = facts.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("dataset").GetString()!);
        var air1 = tables["default.main.air1"];
        air1.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        air1.GetProperty("rowFilters").EnumerateArray().Select(f => f.GetProperty("description").GetString())
            .ShouldAllBe(d => d == "Row filter applies (defined by data owner)." || d == "Row filter applies (depends on an attribute of the caller).");
        tables.ContainsKey("default.main.client").ShouldBeTrue();
        tables["default.main.client"].GetProperty("rowFilterApplied").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task WebSql_FactsAgreeWithGraphQlFactsForTheSameTable()
    {
        using var sql = await WebSqlAsync(true);
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ a: default_main_air1(first: 20) { id } }",
            extensions = new { autheris = new { datasetFacts = true } },
        });
        using var graphQl = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = graphQl.RootElement.GetProperty("extensions").GetProperty("autheris").GetProperty("roots").GetProperty("a");

        var air1 = sql.RootElement.GetProperty("datasetFacts").GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("dataset").GetString() == "default.main.air1");
        air1.GetProperty("rowFilterApplied").GetBoolean().ShouldBe(root.GetProperty("rowFilterApplied").GetBoolean());
        air1.GetProperty("tags").GetRawText().ShouldBe(root.GetProperty("tags").GetRawText());
        air1.GetProperty("rowFilters").GetRawText().ShouldBe(root.GetProperty("rowFilters").GetRawText());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WebSql_WithoutFlagOrWithFalse_HasNoDatasetFacts()
    {
        using var absent = await WebSqlAsync(null);
        using var off = await WebSqlAsync(false);

        absent.RootElement.TryGetProperty("datasetFacts", out _).ShouldBeFalse();
        off.RootElement.TryGetProperty("datasetFacts", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WebSql_FactsDoNotEchoSqlOrParameters()
    {
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new
        {
            sql = "SELECT c.client_id FROM default.main.client c WHERE c.crane_serial_number <> @marker",
            parameters = new { marker = Marker },
            datasetFacts = true,
        });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var doc = JsonDocument.Parse(text);
        var facts = doc.RootElement.GetProperty("datasetFacts").GetRawText();
        facts.ShouldNotContain(Marker);
        facts.ToUpperInvariant().ShouldNotContain("SELECT");
        facts.ShouldNotContain("marker");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WebSql_NonBooleanFlag_IsRejectedWithoutFacts()
    {
        var response = await _fixture.Client().PostAsJsonAsync("/api/v1/sql", new { sql = Sql, parameters = new { marker = -1 }, datasetFacts = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task McpQuerySql_WithFlag_ReportsSameFactsAsWebSql()
    {
        using var sql = await WebSqlAsync(true);
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_sql",
            new { query = Sql, parameters = new { marker = -1 }, datasetFacts = true });

        isError.ShouldBeFalse(payload.ToString());
        payload.GetProperty("datasetFacts").GetRawText().ShouldBe(sql.RootElement.GetProperty("datasetFacts").GetRawText());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQuerySql_WithoutFlag_HasNoDatasetFacts()
    {
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_sql", new { query = Sql, parameters = new { marker = -1 } });

        isError.ShouldBeFalse(payload.ToString());
        payload.TryGetProperty("datasetFacts", out _).ShouldBeFalse();
    }
}
