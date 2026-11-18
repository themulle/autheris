namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.Catalog.Discovery;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Issue #799: <c>describe_dataset(brief: true)</c> returns column signatures (name, type, access, key flag) plus the key order, indexes and
/// required filters, and <c>list_datasets(format: "compact")</c> drops members a list does not need and shortens the description. The
/// numbers are measured on the wire shape the MCP executor serializes (<see cref="McpCatalogWire.ForTool"/> with <see cref="McpResultJson"/>).
/// </summary>
public sealed class Issue799BriefAndCompactTests(ITestOutputHelper output)
{
    private const int WideColumnCount = 287;
    private static readonly TableIdentifier Wide = new("erp", "dbo", "company_settings");

    private static TableMetadata WideTable(int columnCount = WideColumnCount)
    {
        var tableId = Guid.NewGuid();
        var columns = new List<TableColumn>(columnCount);
        var classifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columnCount; i++)
        {
            var id = Guid.NewGuid();
            var name = $"t_col{i:D3}";
            columns.Add(new TableColumn
            {
                Id = id,
                TableId = tableId,
                ColumnName = name,
                DataType = i % 3 == 0 ? "nvarchar(50)" : "int",
                DisplayName = $"Setting {i}",
                Description = $"Accounting setting number {i} of the company master data"
            });
            classifications[name] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = id.ToString(), Code = ClassificationScopes.ConfidentialCode, Source = "OWNER" }];
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
                Description = "One row per company with validity date, name, financial year and many accounting settings. Loaded nightly.",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                Sensitivity = "CONFIDENTIAL",
                RequiredFilterColumns = ["t_col002"]
            },
            Columns = columns,
            ColumnClassifications = classifications,
            CatalogTags = ["erp"],
            PrimaryKeyColumns = ["t_col000", "t_col001"],
            PrimaryKeyDirections = [KeyColumnDirection.Ascending, KeyColumnDirection.Descending],
            Indexes = [new TableIndexDefinition("ix_year", ["t_col004"], false, [KeyColumnDirection.Descending])]
        };
    }

    private static McpDatasetCatalog Catalog(TableMetadata table, Dictionary<string, ColumnAccessLevel>? access = null, params TableMetadata[] more)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(table);
        repo.Tables.AddRange(more);
        var pdp = new FakeAccessDecisionPoint(r => TableAccessDecision.Allowed(
            r.Object.Identifier,
            access ?? r.Object.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
            hasUnconstrainedColumnAllow: true));
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        return new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), Substitute.For<IGatewayExecutionService>(), discoveryService: discovery);
    }

    private static System.Security.Claims.ClaimsPrincipal User => DiscoveryTestBuilder.User("S-1-USER", "tenant-a");

    private static async Task<string> DescribeWireAsync(McpDatasetCatalog catalog, bool brief, McpDescribeNarrowing.Request? narrowing = null)
    {
        var described = await catalog.DescribeDatasetAsync(User, Wide.ToString(), language: null, brief);
        var narrowed = McpDescribeNarrowing.Apply(described, narrowing ?? new McpDescribeNarrowing.Request(null, null, 0, null, null));
        return JsonSerializer.Serialize(McpCatalogWire.ForTool(narrowed, compactList: false), McpResultJson.Options);
    }

    [Fact]
    public async Task Brief_OnAWideTable_KeepsSignaturesKeysAndFilters_AndSavesMostOfTheAnswer()
    {
        var catalog = Catalog(WideTable());

        var full = await DescribeWireAsync(catalog, brief: false);
        var brief = await DescribeWireAsync(catalog, brief: true);

        output.WriteLine($"describe_dataset {WideColumnCount} columns: full {full.Length} chars, brief {brief.Length} chars, saving {100.0 * (full.Length - brief.Length) / full.Length:F1} %");
        brief.Length.ShouldBeLessThan(full.Length / 2);

        using var doc = JsonDocument.Parse(brief);
        var root = doc.RootElement;
        root.GetProperty("view").GetString().ShouldBe(McpDatasetCatalog.BriefView);
        var columns = root.GetProperty("columns");
        columns.GetArrayLength().ShouldBe(WideColumnCount);
        var first = columns[0];
        first.EnumerateObject().Select(p => p.Name).ShouldBe(["name", "type", "access", "primaryKey"]);
        first.GetProperty("primaryKey").GetBoolean().ShouldBeTrue();
        columns[5].EnumerateObject().Select(p => p.Name).ShouldBe(["name", "type", "access"]);
        brief.ShouldNotContain("classifications");
        brief.ShouldNotContain("clearRequestable");
        brief.ShouldNotContain("Accounting setting number");
        root.GetProperty("primaryKeyOrder").EnumerateArray().Select(k => $"{k.GetProperty("column").GetString()} {k.GetProperty("direction").GetString()}")
            .ShouldBe(["t_col000 ASC", "t_col001 DESC"]);
        root.GetProperty("indexes").GetArrayLength().ShouldBe(1);
        root.GetProperty("requiredFilterColumns").EnumerateArray().Select(c => c.GetString()).ShouldBe(["t_col002"]);
    }

    [Fact]
    public async Task Brief_WithColumns_ReturnsOnlyTheNamedSignatures()
    {
        var catalog = Catalog(WideTable());
        McpDescribeNarrowing.TryParse(null, "t_col010,t_col011", null, null, null, out var request).ShouldBeTrue();

        using var doc = JsonDocument.Parse(await DescribeWireAsync(catalog, brief: true, request));

        doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["t_col010", "t_col011"]);
        doc.RootElement.GetProperty("columnTotal").GetInt32().ShouldBe(WideColumnCount);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Brief_MaskedKeyColumn_OmitsTheKey_AndReportsMaskAccess()
    {
        var access = WideTable().Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        access["t_col001"] = ColumnAccessLevel.Mask;
        access["t_col003"] = ColumnAccessLevel.Deny;
        var catalog = Catalog(WideTable(), access);

        var brief = await DescribeWireAsync(catalog, brief: true);

        using var doc = JsonDocument.Parse(brief);
        doc.RootElement.TryGetProperty("primaryKeyOrder", out _).ShouldBeFalse();
        var columns = doc.RootElement.GetProperty("columns").EnumerateArray().ToList();
        columns.ShouldNotContain(c => c.GetProperty("name").GetString() == "t_col003");
        columns.Single(c => c.GetProperty("name").GetString() == "t_col001").GetProperty("access").GetString().ShouldBe("mask");
        columns.Any(c => c.TryGetProperty("primaryKey", out _)).ShouldBeFalse();
    }

    [Fact]
    public async Task CompactList_DropsListMembers_AndShortensTheDescription()
    {
        var tables = Enumerable.Range(0, 100).Select(i =>
        {
            var t = DiscoveryTestBuilder.MakeTable("erp", $"tlhsoc{i:D3}");
            return new TableMetadata
            {
                Identifier = t.Identifier,
                OwnerTenantId = t.OwnerTenantId,
                CatalogTags = ["2_internal", "erp"],
                Columns = t.Columns,
                Table = new Table
                {
                    Id = t.Table.Id,
                    SourceName = t.Table.SourceName,
                    SchemaName = t.Table.SchemaName,
                    TableName = t.Table.TableName,
                    DisplayName = t.Table.TableName,
                    Description = $"One row per time type {i}, settlement domain and overtime type with the derived new time type. "
                        + "Controls how overtime is assigned in service settlement. Only in company 184 and only live in the ERP.",
                    DataSourceType = DataSourceType.Sql,
                    IsActive = true,
                    Sensitivity = "INTERNAL"
                }
            };
        }).ToList();
        var catalog = Catalog(tables[0], null, [.. tables.Skip(1)]);

        var fullList = await catalog.ListDatasetsAsync(User, null, "erp", 0, 100, null, null, null, "full");
        var compactList = await catalog.ListDatasetsAsync(User, null, "erp", 0, 100, null, null, null, "compact");
        var full = JsonSerializer.Serialize(McpCatalogWire.ForTool(fullList, compactList: false), McpResultJson.Options);
        var compact = JsonSerializer.Serialize(McpCatalogWire.ForTool(compactList, compactList: true), McpResultJson.Options);

        output.WriteLine($"list_datasets 100 datasets: full {full.Length} chars, compact {compact.Length} chars, saving {100.0 * (full.Length - compact.Length) / full.Length:F1} %");
        compact.Length.ShouldBeLessThan(full.Length * 6 / 10);
        using var doc = JsonDocument.Parse(compact);
        var entry = doc.RootElement.GetProperty("datasets")[0];
        entry.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "description", "sensitivity", "accessStatus"]);
        entry.GetProperty("description").GetString().ShouldBe("One row per time type 0, settlement domain and overtime type with the derived new time type.");
        doc.RootElement.GetProperty("total").GetInt32().ShouldBe(100);
        doc.RootElement.TryGetProperty("domains", out _).ShouldBeTrue();
    }

    [Fact]
    public void Compact_KeepsKindAndDisplayName_WhenTheyCarryInformation()
    {
        var list = new McpDatasetList(
            [
                new McpDatasetSummary("procedure:refresh_totals", "Refresh totals", "Recomputes totals.", "NORMAL", 3, null, null, "GRANTED", Kind: "procedure"),
                new McpDatasetSummary("erp.dbo.orders", "Customer orders", "Orders.", "INTERNAL", 12, "erp_dbo_orders", ["erp"], "GRANTED")
            ],
            Total: 2,
            Truncated: false);

        var compact = McpCatalogWire.Compact(list);

        compact.Datasets[0].Kind.ShouldBe("procedure");
        compact.Datasets[1].Kind.ShouldBeNull();
        compact.Datasets[1].DisplayName.ShouldBe("Customer orders");
    }
    [Fact]
    [Trait("Category", "Security")]
    public async Task LegacyPath_Brief_CarriesKeyOrder_AndProjectsRequiredFilters()
    {
        var table = Issue807LegacyDescribeTests.Table(["device_id", "secret_key"], [["ts"], ["secret_key"]]);

        var brief = await Issue807LegacyDescribeTests.DescribeAsync(table, brief: true, "device_id", "ts");
        var wire = JsonSerializer.Serialize(McpCatalogWire.ForTool(brief, compactList: false), McpResultJson.Options);

        brief.View.ShouldBe(McpDatasetCatalog.BriefView);
        brief.PrimaryKeyOrder.ShouldNotBeNull().ShouldBe([new McpKeyColumn("device_id", "ASC"), new McpKeyColumn("ts", "DESC")]);
        brief.RequiredFilterColumns.ShouldBe(["device_id"]);
        brief.RequiredFilterColumnGroups.ShouldNotBeNull().Select(g => string.Join(",", g)).ShouldBe(["ts"]);
        brief.Columns.Select(c => (c.Name, c.Access)).ShouldBe([("device_id", "clear"), ("ts", "clear")]);
        wire.ShouldNotContain("secret_key");
    }

    private static GatewayMcpQueryExecutor Executor(IMcpDatasetCatalog catalog) => new(
        Substitute.For<IRequestExecutorProvider>(), Substitute.For<IGatewayExecutionService>(), NullLogger<GatewayMcpQueryExecutor>.Instance,
        McpRowCapTests.Options(100), datasetCatalog: catalog, persistedToolValidator: new PersistedToolValidator());

    private static McpToolDefinition Tool(string name) => McpDatasetTools.Definitions().First(t => t.Name == name);

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    [Fact]
    public async Task Executor_DescribeBrief_SerializesTheBriefWireShape()
    {
        var described = await Catalog(WideTable(10)).DescribeDatasetAsync(User, Wide.ToString(), language: null, brief: true);
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.DescribeDatasetAsync(Arg.Any<ClaimsPrincipal>(), Wide.ToString(), Arg.Any<string?>(), true, Arg.Any<CancellationToken>()).Returns(described);

        var json = await Executor(catalog).ExecuteOperationAsync(Tool(McpDatasetTools.DescribeDataset), $$"""{"dataset":"{{Wide}}","brief":true}""", Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("view").GetString().ShouldBe(McpDatasetCatalog.BriefView);
        doc.RootElement.TryGetProperty("examples", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("domain", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("columns")[1].EnumerateObject().Select(p => p.Name).ShouldBe(["name", "type", "access", "primaryKey"]);
        doc.RootElement.GetProperty("primaryKeyOrder").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Executor_ListCompact_SerializesTheCompactWireShape()
    {
        var list = new McpDatasetList(
            [new McpDatasetSummary("erp.dbo.orders", "orders", "Customer orders. One row per order.", "INTERNAL", 12, "erp_dbo_orders", ["erp"], "GRANTED")],
            Total: 1,
            Truncated: false);
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.ListDatasetsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool?>(), "compact", Arg.Any<CancellationToken>())
            .Returns(list);

        var json = await Executor(catalog).ExecuteOperationAsync(Tool(McpDatasetTools.ListDatasets), """{"format":"compact"}""", Session());

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("datasets")[0];
        entry.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "description", "sensitivity", "accessStatus"]);
        entry.GetProperty("description").GetString().ShouldBe("Customer orders.");
        doc.RootElement.TryGetProperty("domains", out _).ShouldBeTrue();
    }
}
