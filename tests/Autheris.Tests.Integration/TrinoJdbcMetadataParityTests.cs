namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

/// <summary>
/// The Trino <c>system.jdbc.tables</c> listing equals the discovery API visible set of the same principal (CI only).
/// Run: dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~TrinoJdbcMetadataParityTests"
/// </summary>
[Trait("Category", "Security")]
public sealed class TrinoJdbcMetadataParityTests : IClassFixture<RowFilterChannelParityTests.Fixture>
{
    private readonly RowFilterChannelParityTests.Fixture _fixture;

    public TrinoJdbcMetadataParityTests(RowFilterChannelParityTests.Fixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Security_TrinoJdbcMetadata_ParityWithDiscoveryEndpoint()
    {
        // The endpoint's visible set comes from the Rest discovery surface; page through every cursor.
        var expected = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/catalog/objects?pageSize=200" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var discovery = await _fixture.Client().GetAsync(url);
            discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var page = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
            expected.AddRange(page.RootElement.GetProperty("items").EnumerateArray()
                .Where(i => !string.Equals(i.GetProperty("kind").GetString(), "procedure", StringComparison.OrdinalIgnoreCase))
                .Select(i => $"{i.GetProperty("domain").GetString()}.{i.GetProperty("schema").GetString()}.{i.GetProperty("name").GetString()}".ToUpperInvariant()));
            cursor = page.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        expected.Sort(StringComparer.Ordinal);
        expected.ShouldNotBeEmpty();

        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("X-Trino-User", "test-user");
        client.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
        var response = await client.PostAsync(
            "/v1/statement",
            new StringContent("SELECT TABLE_CAT, TABLE_SCHEM, TABLE_NAME FROM system.jdbc.tables", Encoding.UTF8, "text/plain"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED", root.ToString());

        var actual = root.TryGetProperty("data", out var data)
            ? data.EnumerateArray().Select(r => $"{r[0].GetString()}.{r[1].GetString()}.{r[2].GetString()}".ToUpperInvariant()).Order().ToList()
            : new List<string>();
        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task Security_TrinoJdbcColumns_FilterBySchemaAndTable_MatchesDiscoveryColumns()
    {
        var discovery = await _fixture.Client().GetAsync("/api/v1/catalog/objects?pageSize=200");
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        var items = page.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => !string.Equals(i.GetProperty("kind").GetString(), "procedure", StringComparison.OrdinalIgnoreCase))
            .ToList();
        items.Count.ShouldBeGreaterThanOrEqualTo(2);

        var air1 = items.Single(i => string.Equals(i.GetProperty("name").GetString(), "air1", StringComparison.OrdinalIgnoreCase));
        var expectedColumns = air1.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!)
            .ToList();
        expectedColumns.ShouldNotBeEmpty();

        var client = _fixture.Client();
        client.DefaultRequestHeaders.Add("X-Trino-User", "test-user");
        client.DefaultRequestHeaders.Add("X-Trino-Wait-Timeout", "30s");
        var response = await client.PostAsync(
            "/v1/statement",
            new StringContent(
                "SELECT TABLE_CAT, TABLE_SCHEM, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION FROM system.jdbc.columns WHERE TABLE_SCHEM = 'main' AND TABLE_NAME = 'air1' ORDER BY ORDINAL_POSITION",
                Encoding.UTF8,
                "text/plain"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED", root.ToString());

        var rows = root.GetProperty("data").EnumerateArray().ToList();
        rows.Select(r => $"{r[0].GetString()}.{r[1].GetString()}.{r[2].GetString()}").Distinct().ShouldBe(["default.main.air1"]);
        rows.Select(r => r[3].GetString()!).ToList().ShouldBe(expectedColumns);
        rows.Select(r => r[4].GetInt32()).ToList().ShouldBe(Enumerable.Range(1, expectedColumns.Count).ToList());
    }
}
