namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class McpSemanticToolsTests
{
    [Fact]
    public void Definitions_ExposesListAndDescribeMetricsTools()
    {
        var defs = McpSemanticTools.Definitions();
        defs.ShouldContain(d => d.Name == "list_metrics");
        defs.ShouldContain(d => d.Name == "describe_metric");
    }

    [Fact]
    public async Task ExecuteListAndDescribeMetrics_ReturnsAccurateJson()
    {
        var catalog = new InMemorySemanticCatalog();
        var manifest = new DbtSemanticManifest(
            SemanticModels: new List<DbtSemanticModel>(),
            Metrics: new List<DbtMetric>
            {
                new DbtMetric(
                    Name: "arr",
                    Type: "simple",
                    Description: "Annual Recurring Revenue",
                    Label: "ARR",
                    Filter: null,
                    TypeParams: null,
                    Dimensions: new[] { "region", "product" },
                    Measures: new[] { "revenue" },
                    Meta: new Dictionary<string, string> { ["unit"] = "EUR" }
                )
            });
        await catalog.RegisterManifestAsync(manifest);

        // 1. List metrics
        var listJson = await McpSemanticTools.ExecuteListMetricsAsync(catalog, null);
        using var listDoc = JsonDocument.Parse(listJson);
        listDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        listDoc.RootElement.GetProperty("metrics")[0].GetProperty("name").GetString().ShouldBe("arr");

        // 2. Describe metric
        var describeJson = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, """{ "metric": "arr" }""");
        using var descDoc = JsonDocument.Parse(describeJson);
        descDoc.RootElement.GetProperty("label").GetString().ShouldBe("ARR");
        descDoc.RootElement.GetProperty("meta").GetProperty("unit").GetString().ShouldBe("EUR");

        // 3. Describe non-existent metric returns error JSON
        var notFoundJson = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, """{ "metric": "non_existent" }""");
        notFoundJson.ShouldContain("not found");
    }
}
