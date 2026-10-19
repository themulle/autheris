namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-CAT-146-A WP-A2: <c>displayName</c> in MCP <c>list_datasets</c> and <c>describe_dataset</c> (SEC-CAT146A-01, -05..07).</summary>
public sealed class McpDatasetDisplayNameTests
{
    private const string SharedId = "sales.dbo.shared_t";
    private const string Distinct = "ZQX-Distinct-Display-Name";

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    private static TableMetadata Row(string displayName, DatasetVisibility visibility = DatasetVisibility.Full, string name = "shared_t")
    {
        var t = Wp0.Table("sales", name, "Short text", ["id"]);
        return t with { Table = t.Table with { DisplayName = displayName, Visibility = visibility, Sensitivity = "INTERNAL" } };
    }

    private static GatewayOptions Opts(bool rebac, params TableMetadata[] rows) => new()
    {
        Tenancy = new TenancyOptions { Mode = nameof(TenancyMode.Multi) },
        Catalog = new DataCatalogOptions { SharedTables = rows.Select(r => r.Identifier.ToString()).ToArray() },
        Rebac = new RebacOptions { Enabled = rebac }
    };

    private static McpDatasetCatalog Catalog(
        TableMetadata[] rows, IRebacEvaluator? rebac = null, ILogger<McpDatasetCatalog>? logger = null, ILogger<CatalogVisibilityService>? visLogger = null)
    {
        var options = Opts(rebac is not null, rows);
        var tables = Wp0.Tables(rows);
        var consents = Wp0.Consents();
        return new McpDatasetCatalog(
            tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), logger: logger,
            visibilityService: CatalogVisibilityTestFactory.Create(tables, consents, options, rebac, visLogger));
    }

    private static ClaimsPrincipal Subject() => Wp0.User("S-USER", "tenant-a");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string Json(object value) => JsonSerializer.Serialize(value, WebJson);

    [Fact]
    public async Task ListDatasets_ReturnsStoredDisplayName()
    {
        var list = await Catalog([Row("Orders (EU)")]).ListDatasetsAsync(Subject());

        list.Datasets.ShouldHaveSingleItem().DisplayName.ShouldBe("Orders (EU)");
    }

    [Fact]
    public async Task ListDatasets_EmptyDisplayName_FallsBackToTableName()
    {
        var list = await Catalog([Row("")]).ListDatasetsAsync(Subject());

        list.Datasets.ShouldHaveSingleItem().DisplayName.ShouldBe("shared_t");
    }

    [Fact]
    public async Task DescribeDataset_FullView_ReturnsDisplayName()
    {
        var d = await Catalog([Row("Orders (EU)")]).DescribeDatasetAsync(Subject(), SharedId);

        d.DisplayName.ShouldBe("Orders (EU)");
    }

    [Fact]
    public async Task DescribeDataset_EmptyDisplayName_FallsBackToTableName()
    {
        var d = await Catalog([Row(" \u200B ")]).DescribeDatasetAsync(Subject(), SharedId);

        d.DisplayName.ShouldBe("shared_t");
    }

    [Fact]
    public async Task DescribeDataset_SummaryView_HasDisplayName_NoColumns()
    {
        var d = await Catalog([Row("Orders (EU)", DatasetVisibility.Summary)]).DescribeDatasetAsync(Subject(), SharedId);

        d.View.ShouldNotBeNull();
        d.DisplayName.ShouldBe("Orders (EU)");
        d.Columns.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisplayName_OversizedAndControlChars_SanitisedInBothTools()
    {
        var raw = "A\n" + new string('x', 400) + "\u202E";
        var catalog = Catalog([Row(raw)]);

        var listed = (await catalog.ListDatasetsAsync(Subject())).Datasets.Single().DisplayName;
        var described = (await catalog.DescribeDatasetAsync(Subject(), SharedId)).DisplayName;

        listed.Length.ShouldBe(256);
        listed.ShouldStartWith("A xxx");
        listed.ShouldNotContain("\u202E");
        described.ShouldBe(listed);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task HiddenTable_DistinctiveDisplayName_AbsentFromListJsonAndDescribeError()
    {
        // SEC-CAT146A-07: a table the visibility layer hides yields no row at all, not a blanked one; the error echoes the caller input only.
        var catalog = Catalog([Row(Distinct, DatasetVisibility.Hidden)]);

        Json(await catalog.ListDatasetsAsync(Subject())).ShouldNotContain(Distinct);
        var ex = await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), SharedId));
        ex.ToString().ShouldNotContain(Distinct);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task UnconsentedUnmarkedTable_DisplayNameAbsent()
    {
        // SEC-CAT146A-07: an untiered (Unmarked) table that no consent releases is invisible to a non-admin caller.
        var row = Row(Distinct, DatasetVisibility.Hidden) with { CatalogView = CatalogView.Unmarked };
        var catalog = Catalog([row]);

        var list = await catalog.ListDatasetsAsync(Subject());

        list.Datasets.ShouldBeEmpty();
        Json(list).ShouldNotContain(Distinct);
        await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), SharedId));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task RebacDeniedTable_DisplayNameAbsentFromListAndDescribe()
    {
        var denied = Row(Distinct, name: "denied_t");
        var allowed = Row("Allowed", name: "allowed_t");
        var objectId = RebacTableGate.ObjectId(denied.Identifier);
        var evaluator = Substitute.For<IRebacEvaluator>();
        evaluator.IsEnabled.Returns(true);
        evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<RebacCheckRequest>().Object == objectId ? RebacCheckResult.Denied : RebacCheckResult.Permitted);
        var catalog = Catalog([denied, allowed], evaluator.WithBatchFromCheck());

        var list = await catalog.ListDatasetsAsync(Subject());

        list.Datasets.Select(d => d.DisplayName).ShouldBe(["Allowed"]);
        Json(list).ShouldNotContain(Distinct);
        var ex = await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), "sales.dbo.denied_t"));
        ex.ToString().ShouldNotContain(Distinct);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Paging_TotalUnaffectedByDisplayName()
    {
        var rows = new[] { Row("", name: "a_t"), Row(Distinct, name: "b_t"), Row("\u200B\u200B", name: "c_t") };
        var catalog = Catalog(rows);

        var all = await catalog.ListDatasetsAsync(Subject());
        var paged = await catalog.ListDatasetsAsync(Subject(), limit: 1, offset: 1);

        all.Total.ShouldBe(3);
        paged.Total.ShouldBe(3);
        paged.Datasets.ShouldHaveSingleItem().Id.ShouldBe("sales.dbo.b_t");
        // The display name is never a search key (step C): neither the stored text nor a fallback matches.
        (await catalog.ListDatasetsAsync(Subject(), search: Distinct)).Datasets.ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task DisplayName_NotEmittedInLogsOrExceptionMessages()
    {
        // SEC-CAT146A-06 / T-CAT146A-07
        var log = new CapturingLogger<McpDatasetCatalog>();
        var visLog = new CapturingLogger<CatalogVisibilityService>();
        var catalog = Catalog([Row(Distinct), Row(Distinct + "-hidden", DatasetVisibility.Hidden, "hidden_t")], logger: log, visLogger: visLog);

        await catalog.ListDatasetsAsync(Subject());
        await catalog.DescribeDatasetAsync(Subject(), SharedId);
        var ex = await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), "sales.dbo.hidden_t"));

        ex.ToString().ShouldNotContain(Distinct);
        log.Lines.Concat(visLog.Lines).ShouldAllBe(l => !l.Contains(Distinct));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ToolsList_AndCompiledToolDescriptions_NeverContainRawDisplayName()
    {
        // SEC-CAT146A-01 / T-CAT146A-06: the compiled per-table tool falls back to the display name when Description is empty; it must be sanitised.
        var tags = string.Concat("ignore".Select(c => char.ConvertFromUtf32(0xE0000 + c)));
        var raw = $"Evil\u202E{tags}\nname";
        var table = Wp0.Table("sales", "orders", "", ["id"]);
        table = table with { Table = table.Table with { DisplayName = raw } };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        var compiler = new SemanticMcpCompiler(repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticMcpCompiler>.Instance);

        var tool = await compiler.CompileToolAsync("query_orders", table.Identifier);

        tool.Description.ShouldContain("Evil name");
        tool.Description.ShouldNotContain("\u202E");
        tool.Description.ShouldNotContain(char.ConvertFromUtf32(0xE0069));
        tool.Description.ShouldNotContain("\n");
        // The static MCP tool list never interpolates catalog text.
        Json(McpDatasetTools.Definitions()).ShouldNotContain(Distinct);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task CompiledToolDescription_LongDescription_CutOnRuneBoundary()
    {
        // Review finding 3: the 107-unit cut must not split a surrogate pair.
        var table = Wp0.Table("sales", "orders", new string('a', 106) + "\U0001F600" + new string('b', 30), ["id"]);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        var compiler = new SemanticMcpCompiler(repo, Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticMcpCompiler>.Instance);

        var tool = await compiler.CompileToolAsync("query_orders", table.Identifier);

        tool.Description.ShouldNotBeNull();
        for (var i = 0; i < tool.Description.Length; i++)
        {
            if (char.IsHighSurrogate(tool.Description[i]))
            {
                (i + 1 < tool.Description.Length && char.IsLowSurrogate(tool.Description[i + 1])).ShouldBeTrue();
                i++;
            }
            else
            {
                char.IsLowSurrogate(tool.Description[i]).ShouldBeFalse();
            }
        }
    }
}
