namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class McpSemanticToolsTests
{
    private static System.Security.Claims.ClaimsPrincipal AdminPrincipal() =>
        new(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "GovernanceAdmin")
        ], "TestAuth"));

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
        var single = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions { Tenancy = new Autheris.Domain.Options.TenancyOptions { Mode = "Single" } });
        var catalog = new InMemorySemanticCatalog(single);
        var tenancy = new SemanticTenancy(Autheris.Domain.Options.TenancyMode.Single.Owner());
        var admin = AdminPrincipal();
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
        await catalog.RegisterManifestAsync(manifest, Autheris.Domain.Common.TenantId.LegacySingleTenant);

        // 1. List metrics
        var listJson = await McpSemanticTools.ExecuteListMetricsAsync(catalog, admin, tenancy, null);
        using var listDoc = JsonDocument.Parse(listJson);
        listDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        listDoc.RootElement.GetProperty("metrics")[0].GetProperty("name").GetString().ShouldBe("arr");

        // 2. Describe metric
        var describeJson = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, admin, tenancy, """{ "metric": "arr" }""");
        using var descDoc = JsonDocument.Parse(describeJson);
        descDoc.RootElement.GetProperty("label").GetString().ShouldBe("ARR");
        descDoc.RootElement.GetProperty("meta").GetProperty("unit").GetString().ShouldBe("EUR");

        // 3. Describe non-existent metric returns error JSON
        var notFoundJson = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, admin, tenancy, """{ "metric": "non_existent" }""");
        notFoundJson.ShouldContain("not found");
    }

    private static ClaimsPrincipal Analyst(string tenant) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-" + tenant),
            new Claim(ClaimTypes.Name, "analyst"),
            new Claim(ClaimTypes.Role, "Analyst"),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    private static DbtMetric MetricWithFilter(string name) =>
        new(name, "simple", "desc", "Label", "status = 'secret_filter'", "{ \"measure\": \"m\" }", ["region"], ["m"],
            new Dictionary<string, string> { ["model"] = "internal_model" }, Unit: "EUR");

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpListMetrics_TenantScoped()
    {
        var catalog = new InMemorySemanticCatalog();
        var tenancy = new SemanticTenancy(Autheris.Domain.Options.TenancyMode.Multi.Owner());
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("a_metric")], "1.0"), new Autheris.Domain.Common.TenantId("tenant-a"));
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("b_metric")], "1.0"), new Autheris.Domain.Common.TenantId("tenant-b"));

        var listA = await McpSemanticTools.ExecuteListMetricsAsync(catalog, Analyst("tenant-a"), tenancy, null);
        var listB = await McpSemanticTools.ExecuteListMetricsAsync(catalog, Analyst("tenant-b"), tenancy, null);

        listA.ShouldContain("a_metric");
        listA.ShouldNotContain("b_metric");
        listB.ShouldContain("b_metric");
        listB.ShouldNotContain("a_metric");
        var describeForeign = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, Analyst("tenant-a"), tenancy, """{ "metric": "b_metric" }""");
        describeForeign.ShouldContain("not found");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpListMetrics_NoSid_IsEmpty_InBothModes()
    {
        var catalog = new InMemorySemanticCatalog();
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("a_metric")], "1.0"), new Autheris.Domain.Common.TenantId("tenant-a"));
        var noSid = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Name, "x")], "TestAuth"));

        (await McpSemanticTools.ExecuteListMetricsAsync(catalog, noSid, new SemanticTenancy(Autheris.Domain.Options.TenancyMode.Multi.Owner()), null)).ShouldNotContain("a_metric");
        (await McpSemanticTools.ExecuteListMetricsAsync(catalog, noSid, new SemanticTenancy(Autheris.Domain.Options.TenancyMode.Single.Owner()), null)).ShouldNotContain("a_metric");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task McpDescribeMetric_NonAdmin_NoFilterTemplate()
    {
        var catalog = new InMemorySemanticCatalog();
        var tenancy = new SemanticTenancy(Autheris.Domain.Options.TenancyMode.Multi.Owner());
        await catalog.RegisterManifestAsync(new DbtSemanticManifest([], [MetricWithFilter("a_metric")], "1.0"), new Autheris.Domain.Common.TenantId("tenant-a"));

        var analystList = await McpSemanticTools.ExecuteListMetricsAsync(catalog, Analyst("tenant-a"), tenancy, null);
        var analystDescribe = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, Analyst("tenant-a"), tenancy, """{ "metric": "a_metric" }""");
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim("tenant_id", "tenant-a")
        ], "TestAuth"));
        var adminDescribe = await McpSemanticTools.ExecuteDescribeMetricAsync(catalog, admin, tenancy, """{ "metric": "a_metric" }""");

        foreach (var json in new[] { analystList, analystDescribe })
        {
            json.ShouldContain("a_metric");
            json.ShouldNotContain("secret_filter");
            json.ShouldNotContain("internal_model");
        }

        adminDescribe.ShouldContain("secret_filter");
    }
}
