namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #820: a <c>describe_dataset</c> answer above <c>Mcp:MaxTokensPerCall</c> is shortened at column boundaries instead of being
/// replaced by the notice. Dataset-level members stay, <c>_meta.nextColumnOffset</c> continues with <c>columnOffset</c>. The answers
/// are the wire shape the MCP executor serializes (real catalog, <see cref="McpDescribeNarrowing"/>, <see cref="McpCatalogWire.ForTool"/>).
/// </summary>
public sealed class Issue820DescribeTokenBudgetTests
{
    private const int DefaultMaxTokens = 4096;
    private const int WideColumnCount = 300;
    private static readonly TableIdentifier Wide = new("erp", "dbo", "company_settings");
    private static readonly string DatasetId = Wide.ToString();

    private static TableMetadata WideTable()
    {
        var tableId = Guid.NewGuid();
        var columns = new List<TableColumn>(WideColumnCount);
        for (var i = 0; i < WideColumnCount; i++)
        {
            columns.Add(new TableColumn
            {
                Id = Guid.NewGuid(),
                TableId = tableId,
                ColumnName = $"t_col{i:D3}",
                DataType = i % 3 == 0 ? "nvarchar(50)" : "int",
                DisplayName = $"Setting {i}",
                Description = $"Accounting setting number {i} of the company master data"
            });
        }

        return new TableMetadata
        {
            Identifier = Wide,
            OwnerTenantId = "tenant-a",
            Table = new Table
            {
                Id = tableId,
                SourceName = "erp",
                SchemaName = "dbo",
                TableName = "company_settings",
                DisplayName = "company_settings",
                Description = "One row per company with validity date, name, financial year and many accounting settings.",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                Sensitivity = "INTERNAL",
                RequiredFilterColumns = ["t_col002"]
            },
            Columns = columns,
            CatalogTags = ["erp"],
            PrimaryKeyColumns = ["t_col000", "t_col001"],
            PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Descending],
            Indexes = [new TableIndexDefinition("ix_year", ["t_col004"], false, [KeyColumnDirection.Descending])]
        };
    }

    private static McpDatasetCatalog Catalog(Dictionary<string, ColumnAccessLevel>? access = null)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(WideTable());
        var pdp = new FakeAccessDecisionPoint(r => TableAccessDecision.Allowed(
            r.Object.Identifier,
            access ?? r.Object.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
            hasUnconstrainedColumnAllow: true));
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        return new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), Substitute.For<IGatewayExecutionService>(), discoveryService: discovery);
    }

    private static System.Security.Claims.ClaimsPrincipal User => DiscoveryTestBuilder.User("S-1-USER", "tenant-a");

    private static async Task<string> DescribeWireAsync(McpDatasetCatalog catalog, bool brief = false, int columnOffset = 0, IReadOnlyList<string>? names = null)
    {
        var described = await catalog.DescribeDatasetAsync(User, DatasetId, language: null, brief);
        var narrowed = McpDescribeNarrowing.Apply(described, new McpDescribeNarrowing.Request(null, names, columnOffset, null, null));
        return JsonSerializer.Serialize(McpCatalogWire.ForTool(narrowed, compactList: false), McpResultJson.Options);
    }

    private static string Arguments(int columnOffset = 0) =>
        columnOffset == 0 ? $$"""{"dataset":"{{DatasetId}}"}""" : $$"""{"dataset":"{{DatasetId}}","columnOffset":{{columnOffset}}}""";

    /// <summary>The budget step of the guardrail: shortened when over budget, otherwise the answer unchanged.</summary>
    private static string Budgeted(string json, string arguments, int maxTokens = DefaultMaxTokens) =>
        json.Length <= maxTokens * 4
            ? json
            : McpBudgetShortening.TryShorten(McpDatasetTools.DescribeDataset, json, arguments, maxTokens * 4, maxTokens, out var shortened)
                ? shortened
                : throw new ShouldAssertException("describe_dataset was not shortened.");

    private static List<string> ColumnNames(JsonElement root) =>
        root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();

    /// <summary>Reads every page with <c>columnOffset</c> = <c>_meta.nextColumnOffset</c> until an answer is not shortened.</summary>
    private static async Task<List<string>> ReadAllPagesAsync(McpDatasetCatalog catalog, bool brief = false, int maxTokens = DefaultMaxTokens)
    {
        var names = new List<string>();
        var offset = 0;
        for (var page = 0; page < WideColumnCount; page++)
        {
            var json = Budgeted(await DescribeWireAsync(catalog, brief, offset), Arguments(offset), maxTokens);
            json.Length.ShouldBeLessThanOrEqualTo(maxTokens * 4);
            using var doc = JsonDocument.Parse(json);
            names.AddRange(ColumnNames(doc.RootElement));
            if (!doc.RootElement.TryGetProperty("_meta", out var meta))
            {
                return names;
            }

            var next = meta.GetProperty("nextColumnOffset").GetInt32();
            next.ShouldBeGreaterThan(offset);
            offset = next;
        }

        throw new ShouldAssertException("Paging did not terminate.");
    }

    [Fact]
    public async Task WideTable_UnderTheDefaultBudget_KeepsDatasetLevelMembers_AColumnPrefix_AndTheMarker()
    {
        var full = await DescribeWireAsync(Catalog());
        full.Length.ShouldBeGreaterThan(DefaultMaxTokens * 4);

        var shortened = Budgeted(full, Arguments());

        shortened.Length.ShouldBeLessThanOrEqualTo(DefaultMaxTokens * 4);
        using var doc = JsonDocument.Parse(shortened);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().ShouldBe(DatasetId);
        root.GetProperty("description").GetString()!.ShouldStartWith("One row per company");
        root.GetProperty("accessStatus").ValueKind.ShouldBe(JsonValueKind.String);
        root.GetProperty("primaryKeyOrder").GetArrayLength().ShouldBe(2);
        root.GetProperty("indexes").GetArrayLength().ShouldBe(1);
        root.GetProperty("requiredFilterColumns").EnumerateArray().Select(e => e.GetString()).ShouldBe(["t_col002"]);
        root.GetProperty("columnTotal").GetInt32().ShouldBe(WideColumnCount);

        var names = ColumnNames(root);
        names.Count.ShouldBeGreaterThan(10);
        names.Count.ShouldBeLessThan(WideColumnCount);
        names.ShouldBe(Enumerable.Range(0, names.Count).Select(i => $"t_col{i:D3}"));

        var meta = root.GetProperty("_meta");
        meta.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        meta.GetProperty("reason").GetString().ShouldBe("TOKEN_BUDGET_EXCEEDED");
        meta.GetProperty("returned").GetInt32().ShouldBe(names.Count);
        meta.GetProperty("omitted").GetInt32().ShouldBe(WideColumnCount - names.Count);
        meta.GetProperty("nextColumnOffset").GetInt32().ShouldBe(names.Count);
        var note = meta.GetProperty("note").GetString()!;
        note.ShouldContain("columnOffset");
        note.ShouldContain("columns");
        note.ShouldContain("brief");
    }

    [Fact]
    public async Task Continuation_ReturnsTheRemainingColumns_WithoutOverlapOrGap()
    {
        var names = await ReadAllPagesAsync(Catalog());

        names.ShouldBe(Enumerable.Range(0, WideColumnCount).Select(i => $"t_col{i:D3}"));
    }

    [Fact]
    public async Task Brief_OverBudget_IsShortenedInTheBriefShape_AndPagesThroughAllColumns()
    {
        const int maxTokens = 1024;
        var brief = await DescribeWireAsync(Catalog(), brief: true);
        brief.Length.ShouldBeGreaterThan(maxTokens * 4);

        using (var doc = JsonDocument.Parse(Budgeted(brief, Arguments(), maxTokens)))
        {
            var root = doc.RootElement;
            root.GetProperty("view").GetString().ShouldBe(McpDatasetCatalog.BriefView);
            root.GetProperty("primaryKeyOrder").GetArrayLength().ShouldBe(2);
            root.GetProperty("columns")[0].EnumerateObject().Select(p => p.Name).ShouldBe(["name", "type", "access", "primaryKey"]);
            root.GetProperty("_meta").GetProperty("note").GetString()!.ShouldNotContain("brief");
        }

        (await ReadAllPagesAsync(Catalog(), brief: true, maxTokens)).ShouldBe(Enumerable.Range(0, WideColumnCount).Select(i => $"t_col{i:D3}"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenColumns_AppearOnNoPage_AndDoNotShiftTheCursor()
    {
        var access = WideTable().Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var hidden = Enumerable.Range(0, WideColumnCount).Where(i => i % 7 == 3).Select(i => $"t_col{i:D3}").ToHashSet();
        foreach (var name in hidden)
        {
            access[name] = ColumnAccessLevel.Deny;
        }

        var catalog = Catalog(access);
        var visible = Enumerable.Range(0, WideColumnCount).Select(i => $"t_col{i:D3}").Where(n => !hidden.Contains(n)).ToList();

        using (var first = JsonDocument.Parse(Budgeted(await DescribeWireAsync(catalog), Arguments())))
        {
            first.RootElement.GetProperty("columnTotal").GetInt32().ShouldBe(visible.Count);
        }

        var names = await ReadAllPagesAsync(catalog);

        names.ShouldBe(visible);
        names.ShouldNotContain(n => hidden.Contains(n));
    }

    [Fact]
    public async Task NamedColumnSelection_OverBudget_HasNoOffsetCursor_AndNamesTheColumnsArgument()
    {
        const int maxTokens = 1024;
        var selected = Enumerable.Range(0, 200).Select(i => $"t_col{i:D3}").ToList();
        var json = await DescribeWireAsync(Catalog(), names: selected);
        var arguments = $$"""{"dataset":"{{DatasetId}}","columns":{{JsonSerializer.Serialize(selected)}}}""";

        McpBudgetShortening.TryShorten(McpDatasetTools.DescribeDataset, json, arguments, maxTokens * 4, maxTokens, out var shortened).ShouldBeTrue();

        using var doc = JsonDocument.Parse(shortened);
        var meta = doc.RootElement.GetProperty("_meta");
        meta.TryGetProperty("nextColumnOffset", out _).ShouldBeFalse();
        meta.GetProperty("note").GetString()!.ShouldContain("columns");
        meta.GetProperty("note").GetString()!.ShouldNotContain("columnOffset");
        doc.RootElement.GetProperty("columnTotal").GetInt32().ShouldBe(WideColumnCount);
    }

    [Fact]
    public async Task BudgetBelowTheAnswerWithoutColumns_IsNotShortened()
    {
        var json = await DescribeWireAsync(Catalog());

        McpBudgetShortening.TryShorten(McpDatasetTools.DescribeDataset, json, Arguments(), 400, 100, out _).ShouldBeFalse();
    }

    [Fact]
    public void UnparsableArguments_AreNotShortened() =>
        McpBudgetShortening.TryShorten(McpDatasetTools.DescribeDataset, """{"id":"x","columns":[{"name":"a"},{"name":"b"}]}""", "not json", 30, 8, out _).ShouldBeFalse();

    [Fact]
    public async Task Guardrail_ShortensAnOverBudgetDescription_AndLeavesAnUnderBudgetOneUnchanged()
    {
        var wide = await DescribeWireAsync(Catalog());
        const string small = """{"id":"erp.dbo.small","columns":[{"name":"a","type":"int"}]}""";
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Is<string>(a => a.Contains("company_settings")), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(wide));
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Is<string>(a => a.Contains("erp.dbo.small")), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(small));
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true, MaxTokensPerCall = DefaultMaxTokens } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor,
            decisionPoint: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll(),
            tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());
        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

        var shortened = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.DescribeDataset, Arguments()), session);
        var unchanged = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.DescribeDataset, """{"dataset":"erp.dbo.small"}"""), session);

        shortened.IsSuccess.ShouldBeTrue();
        shortened.TruncatedDueToBudget.ShouldBeTrue();
        using (var doc = JsonDocument.Parse(shortened.ContentJson))
        {
            doc.RootElement.GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(0);
            doc.RootElement.GetProperty("primaryKeyOrder").GetArrayLength().ShouldBe(2);
            doc.RootElement.GetProperty("_meta").GetProperty("nextColumnOffset").GetInt32().ShouldBe(doc.RootElement.GetProperty("columns").GetArrayLength());
        }

        unchanged.TruncatedDueToBudget.ShouldBeFalse();
        unchanged.ContentJson.ShouldBe(small);
    }

    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }
}
