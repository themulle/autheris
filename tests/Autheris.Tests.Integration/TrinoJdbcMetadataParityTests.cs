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
        var discovery = await _fixture.Client().GetAsync("/api/v1/catalog/objects?first=200");
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var page = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        var expected = page.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => !string.Equals(i.GetProperty("kind").GetString(), "procedure", StringComparison.OrdinalIgnoreCase))
            .Select(i => $"{i.GetProperty("domain").GetString()}.{i.GetProperty("schema").GetString()}.{i.GetProperty("name").GetString()}".ToUpperInvariant())
            .Order()
            .ToList();
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
}
