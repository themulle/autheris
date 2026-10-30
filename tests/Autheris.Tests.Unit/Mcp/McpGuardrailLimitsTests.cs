namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>MCP-08: doc tools run under the per-call token budget and <c>search_docs.limit</c> is bounded.</summary>
[Trait("Category", "Security")]
public sealed class McpGuardrailLimitsTests
{
    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

    private static DocSearchResult Hit(string summary) =>
        new(new DocChunkIdentifier("app", "Guide", "doc", "s1"), "app", DocCategory.Guide, "Title", summary, 1.0, [], "doc.md");

    [Theory]
    [InlineData(int.MaxValue, 20)]
    [InlineData(1000, 20)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(7, 7)]
    public async Task SearchDocs_Limit_ClampedTo1To20(int requested, int expected)
    {
        // MCP-08
        var search = Substitute.For<IDocSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<DocAccessScope>(), Arg.Any<DocCategory?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DocSearchResult>>([]));

        await McpDocTools.ExecuteSearchDocsAsync(search, $$"""{"query":"x","limit":{{requested}}}""", Session());

        await search.Received(1).SearchAsync("x", Arg.Any<DocAccessScope>(), Arg.Any<DocCategory?>(), expected, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"query":"x","limit":1.5}""")]
    [InlineData("""{"query":"x","limit":"5"}""")]
    [InlineData("""{"query":"x","limit":99999999999}""")]
    public async Task SearchDocs_LimitNotAnInteger_InvalidParams_NoSearch(string args)
    {
        // MCP-08
        var search = Substitute.For<IDocSearchService>();

        var json = await McpDocTools.ExecuteSearchDocsAsync(search, args, Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("limit");
        search.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task DocTool_OversizedResult_TruncatedToMaxTokensPerCall()
    {
        // MCP-08: doc tools no longer skip the token budget.
        var search = Substitute.For<IDocSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<DocAccessScope>(), Arg.Any<DocCategory?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DocSearchResult>>([Hit(new string('a', 60_000))]));
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = 1000 } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            docSearchService: search);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDocTools.SearchDocs, """{"query":"x"}"""), Session());

        result.IsSuccess.ShouldBeTrue();
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ContentJson.Length.ShouldBeLessThan(4000);
        result.ContentJson.ShouldContain("TOKEN_BUDGET_EXCEEDED");
    }
}
