namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpSearchCatalogTagFilterTests
{
    private static readonly McpSessionContext Session = new(
        "s1",
        "agent",
        "tenant-a",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        UserSid: "S-1-USER",
        Roles: ["GovernanceAdmin"]);

    private static McpToolDefinition SearchTool => new(
        McpDatasetTools.SearchCatalog,
        McpDatasetTools.SearchCatalog,
        "{}",
        TargetGraphQLOperation: McpDatasetTools.SearchCatalog);

    private static CatalogSearchHit Hit(TableIdentifier id) => new(
        TableIdentifier: id,
        DisplayName: id.TableName,
        Description: "d",
        Domain: id.Domain,
        Sensitivity: "NORMAL",
        CombinedScore: 0.9,
        Bm25Score: 0.9,
        VectorScore: null,
        MatchedTerms: ["orders"],
        RelevantColumns: [],
        RelatedJoinPaths: [],
        SuggestedGraphQlField: null);

    private static TableMetadata Meta(TableIdentifier id, params string[] tags) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, Sensitivity = "NORMAL" },
        CatalogTags = tags,
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
        PrimaryKeyColumns = ["id"]
    };

    private static (McpToolExecutionHandler Handler, ICatalogDiscoveryService Search) Create(params (TableIdentifier Id, string[] Tags)[] tables)
    {
        var search = Substitute.For<ICatalogDiscoveryService>();
        search.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<System.Collections.Generic.IReadOnlyList<CatalogSearchHit>>(tables.Select(t => Hit(t.Id)).ToList()));

        var repo = Substitute.For<ITableMetadataRepository>();
        foreach (var (id, tags) in tables)
        {
            repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(Meta(id, tags)));
        }

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            search,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            repo);
        return (handler, search);
    }

    [Fact]
    public async Task SearchCatalog_WithTag_ReturnsOnlyTaggedHits()
    {
        var finance = new TableIdentifier("sales", "public", "orders");
        var other = new TableIdentifier("sales", "public", "events");
        var (handler, _) = Create((finance, ["Finance"]), (other, ["ops"]));

        var json = await handler.ExecuteToolAsync(SearchTool, """{"query":"orders","tag":"finance"}""", Session, CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("total_hits").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("datasets").GetArrayLength().ShouldBe(1);
        json.ShouldContain("orders");
        json.ShouldNotContain("events");
    }

    [Fact]
    public async Task SearchCatalog_WithTag_WidensTheSearchToTheDocumentedMaximum()
    {
        var id = new TableIdentifier("sales", "public", "orders");
        var (handler, search) = Create((id, ["finance"]));

        await handler.ExecuteToolAsync(SearchTool, """{"query":"orders","tag":"finance","limit":3}""", Session, CancellationToken.None);

        await search.Received(1).SearchCatalogDetailedAsync(
            Arg.Is<CatalogSearchQuery>(q => q.Limit == McpToolExecutionHandler.MaxCatalogSearchResults),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchCatalog_WithoutTag_KeepsRequestedLimit()
    {
        var id = new TableIdentifier("sales", "public", "orders");
        var (handler, search) = Create((id, ["finance"]));

        await handler.ExecuteToolAsync(SearchTool, """{"query":"orders","limit":3}""", Session, CancellationToken.None);

        await search.Received(1).SearchCatalogDetailedAsync(
            Arg.Is<CatalogSearchQuery>(q => q.Limit == 3),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>());
    }
}
