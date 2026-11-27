using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>Opt-in <c>extensions.autheris.roots</c> over the real host: consent row filter on <c>default_main_air1</c>.</summary>
public sealed class DatasetFactsExtensionIntegrationTests : IClassFixture<RowFilterChannelParityTests.Fixture>
{
    private readonly RowFilterChannelParityTests.Fixture _fixture;

    public DatasetFactsExtensionIntegrationTests(RowFilterChannelParityTests.Fixture fixture) => _fixture = fixture;

    private async Task<JsonDocument> GraphQlRawAsync(string query, bool datasetFacts)
    {
        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        object body = datasetFacts
            ? new { query, extensions = new { autheris = new { datasetFacts = true } } }
            : new { query };
        var response = await client.PostAsJsonAsync("/graphql", body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private static JsonElement? Roots(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("extensions", out var ext)
        && ext.TryGetProperty("autheris", out var autheris)
        && autheris.TryGetProperty("roots", out var roots)
            ? roots
            : null;

    [Fact]
    public async Task GraphQl_WithFlag_ReportsConsentRowFilterAsFixedText()
    {
        using var doc = await GraphQlRawAsync("{ a: default_main_air1(first: 20) { id } }", datasetFacts: true);

        var a = Roots(doc)!.Value.GetProperty("a");
        a.GetProperty("dataset").GetString().ShouldBe("default.main.air1");
        a.GetProperty("rowFilterApplied").GetBoolean().ShouldBeTrue();
        a.GetProperty("rowFilters").EnumerateArray().Select(f => f.GetProperty("description").GetString())
            .ShouldAllBe(d => d == "Row filter applies (defined by data owner)." || d == "Row filter applies (depends on an attribute of the caller).");
    }

    [Fact]
    public async Task McpQueryGraphql_WithDatasetFacts_ReturnsSameRootsAsGraphQl()
    {
        using var graphQl = await GraphQlRawAsync("{ a: default_main_air1(first: 20) { id } }", datasetFacts: true);
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql",
            new { query = "{ a: default_main_air1(first: 20) { id } }", datasetFacts = true });

        isError.ShouldBeFalse(payload.ToString());
        payload.GetProperty("extensions").GetProperty("autheris").GetProperty("roots").GetRawText()
            .ShouldBe(Roots(graphQl)!.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_WithoutFlag_HasNoAutherisRoots()
    {
        using var doc = await GraphQlRawAsync("{ a: default_main_air1(first: 20) { id } }", datasetFacts: false);

        Roots(doc).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpQueryGraphql_WithoutFlag_HasNoAutherisRoots()
    {
        await using var mcp = await McpTestClient.ConnectAsync(_fixture.Client());

        var (isError, payload) = await McpTestClient.CallJsonAsync(mcp, "query_graphql", new { query = "{ a: default_main_air1(first: 20) { id } }" });

        isError.ShouldBeFalse(payload.ToString());
        payload.TryGetProperty("extensions", out var ext).ShouldBeTrue();
        ext.TryGetProperty("autheris", out var autheris).ShouldBeFalse(autheris.ToString());
    }
}
