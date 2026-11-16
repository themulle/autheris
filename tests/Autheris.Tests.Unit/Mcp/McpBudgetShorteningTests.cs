namespace Autheris.Tests.Unit.Mcp;

using System.Linq;
using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

public sealed class McpBudgetShorteningTests
{
    private static string ListResult(int count, int descriptionLength, int offset = 0)
    {
        var items = Enumerable.Range(0, count)
            .Select(i => new { id = $"sales.public.t{i}", description = new string('d', descriptionLength) })
            .ToList();
        return JsonSerializer.Serialize(new { datasets = items, total = count + 50, truncated = false, offset, limit = count, nextOffset = (int?)null });
    }

    [Fact]
    public void TryShorten_ListResult_KeepsWholeDatasetsThatFit()
    {
        var json = ListResult(100, 400);

        McpBudgetShortening.TryShorten(McpDatasetTools.ListDatasets, json, 8_000, 2_000, out var shortened).ShouldBeTrue();

        shortened.Length.ShouldBeLessThanOrEqualTo(8_000);
        using var doc = JsonDocument.Parse(shortened);
        var kept = doc.RootElement.GetProperty("datasets").GetArrayLength();
        kept.ShouldBeGreaterThan(0);
        kept.ShouldBeLessThan(100);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("nextOffset").GetInt32().ShouldBe(kept);
        var meta = doc.RootElement.GetProperty("_meta");
        meta.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        meta.GetProperty("returned").GetInt32().ShouldBe(kept);
        meta.GetProperty("omitted").GetInt32().ShouldBe(100 - kept);
    }

    [Fact]
    public void TryShorten_ListResultWithOffset_NextOffsetContinuesAfterKeptItems()
    {
        var json = ListResult(100, 400, offset: 200);

        McpBudgetShortening.TryShorten(McpDatasetTools.ListDatasets, json, 8_000, 2_000, out var shortened).ShouldBeTrue();

        using var doc = JsonDocument.Parse(shortened);
        var kept = doc.RootElement.GetProperty("datasets").GetArrayLength();
        doc.RootElement.GetProperty("nextOffset").GetInt32().ShouldBe(200 + kept);
    }

    [Fact]
    public void TryShorten_SearchResult_NamesOnlyExistingParameters()
    {
        var json = ListResult(20, 800);

        McpBudgetShortening.TryShorten(McpDatasetTools.SearchCatalog, json, 4_000, 1_000, out var shortened).ShouldBeTrue();

        using var doc = JsonDocument.Parse(shortened);
        var note = doc.RootElement.GetProperty("_meta").GetProperty("note").GetString()!;
        note.ShouldContain("limit");
        note.ShouldNotContain("offset");
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void TryShorten_OtherTool_IsNotShortened() =>
        McpBudgetShortening.TryShorten("describe_dataset", ListResult(10, 400), 1_000, 250, out _).ShouldBeFalse();

    [Fact]
    public void TryShorten_ResultWithoutDatasets_IsNotShortened() =>
        McpBudgetShortening.TryShorten(McpDatasetTools.ListDatasets, "{\"x\":1}", 1_000, 250, out _).ShouldBeFalse();

    [Fact]
    public void TryShorten_BudgetSmallerThanEnvelope_FallsBack() =>
        McpBudgetShortening.TryShorten(McpDatasetTools.ListDatasets, ListResult(5, 400), 10, 3, out _).ShouldBeFalse();
}
