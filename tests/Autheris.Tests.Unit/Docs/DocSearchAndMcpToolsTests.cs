namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class DocSearchAndMcpToolsTests
{
    private static DocChunk CreateChunk(
        string title,
        string content,
        DocCategory category = DocCategory.Runbook,
        IReadOnlyList<string>? errorCodes = null)
    {
        var id = new DocChunkIdentifier("autheris", category.ToString(), "test-doc", title.ToLowerInvariant().Replace(' ', '-'));
        return new DocChunk(
            Id: id,
            AppId: "autheris",
            Category: category,
            Title: title,
            Content: content,
            Summary: content.Length > 50 ? content[..50] : content,
            Keywords: new[] { "gateway", "token", "incident" },
            ErrorCodes: errorCodes ?? Array.Empty<string>(),
            SourceFile: "docs/test-doc.md",
            AnchorId: id.SectionId,
            UpdatedAt: DateTimeOffset.UtcNow
        );
    }

    [Fact]
    public async Task SearchAsync_BoostsExactErrorCodeMatches()
    {
        var searchService = new InMemoryDocSearchService();
        var chunk1 = CreateChunk("General Auth", "General authentication notes mentioning ERR_TOKEN_EXPIRED in body.");
        var chunk2 = CreateChunk("Token Recovery", "Step by step recovery.", DocCategory.Runbook, new[] { "ERR_TOKEN_EXPIRED" });

        await searchService.IndexChunksAsync(new[] { chunk1, chunk2 });

        var results = await searchService.SearchAsync("ERR_TOKEN_EXPIRED");

        results.Count.ShouldBeGreaterThanOrEqualTo(1);
        results[0].Title.ShouldBe("Token Recovery"); // boosted due to exact error code match
    }

    [Fact]
    public async Task LookupErrorCodeAsync_ReturnsStructuredDefinition()
    {
        var searchService = new InMemoryDocSearchService();
        var chunk = CreateChunk("Permission Denied Runbook", "How to resolve casbin permission errors.",
            DocCategory.Runbook, new[] { "ERR_CASBIN_DENIED" });
        await searchService.IndexChunkAsync(chunk);

        var def = await searchService.LookupErrorCodeAsync("ERR_CASBIN_DENIED");

        def.ShouldNotBeNull();
        def.ErrorCode.ShouldBe("ERR_CASBIN_DENIED");
        def.RunbookRef.ShouldNotBeNull();
    }

    [Fact]
    public async Task McpDocTools_ExecuteSearchAndProcedure_Succeeds()
    {
        var searchService = new InMemoryDocSearchService();
        var chunk = CreateChunk("Disaster Recovery", "Run failover command: ctl failover", DocCategory.Runbook);
        await searchService.IndexChunkAsync(chunk);

        // 1. Search docs tool
        var searchJson = await McpDocTools.ExecuteSearchDocsAsync(searchService, """{ "query": "disaster" }""");
        using var searchDoc = JsonDocument.Parse(searchJson);
        searchDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        var firstResult = searchDoc.RootElement.GetProperty("results")[0];
        firstResult.GetProperty("title").GetString().ShouldBe("Disaster Recovery");

        // 2. Get procedure tool
        var procJson = await McpDocTools.ExecuteGetProcedureAsync(searchService,
            $@"{{ ""identifier"": ""{chunk.Id}"" }}");
        using var procDoc = JsonDocument.Parse(procJson);
        procDoc.RootElement.GetProperty("content").GetString()!.ShouldContain("ctl failover");

        // 3. List topics tool
        var topicsJson = await McpDocTools.ExecuteListDocTopicsAsync(searchService, null);
        using var topDoc = JsonDocument.Parse(topicsJson);
        topDoc.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
    }
}
