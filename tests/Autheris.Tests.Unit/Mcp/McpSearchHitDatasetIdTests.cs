namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #627: a hit of <c>search_catalog</c> and <c>find_relevant_tables</c> names its table with the same
/// <c>domain.schema.table</c> id that <c>list_datasets</c> returns and <c>describe_dataset</c> accepts, so two tables with
/// the same name in two schemas are two distinguishable hits.
/// </summary>
public sealed class McpSearchHitDatasetIdTests
{
    // Same table name in two schemas of one domain; one schema in mixed case (list ids are lower case).
    private static readonly TableIdentifier LiveUnit = new("fleet", "Live", "telematic_unit");
    private static readonly TableIdentifier ArchiveUnit = new("fleet", "archive", "telematic_unit");
    private static readonly TableIdentifier Crane = new("fleet", "Live", "crane");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly GatewayOptions _options = new()
    {
        Catalog = new DataCatalogOptions { SharedTables = ["fleet.live.telematic_unit", "fleet.archive.telematic_unit", "fleet.live.crane"] }
    };

    public McpSearchHitDatasetIdTests()
    {
        IReadOnlyList<TableMetadata> tables =
        [
            Table(LiveUnit, "Telematic units currently installed"),
            Table(ArchiveUnit, "Telematic units removed from service"),
            Table(Crane, "Cranes")
        ];
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tables));
    }

    private static TableMetadata Table(TableIdentifier id, string description) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, Description = description, IsActive = true, DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "crane_id", DataType = "int" }]
    };

    private static readonly McpSessionContext Session = new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["GovernanceAdmin"]);

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin")],
            "MCP"));

    private McpToolExecutionHandler Handler(ICatalogDiscoveryService discovery) => new(
        Substitute.For<IGovernedSqlExecutionService>(),
        Substitute.For<IGovernedDataQueryService>(),
        discovery,
        Substitute.For<IApiDispatcherService>(),
        Substitute.For<ILineageGraphStore>(),
        _metadata);

    private CatalogDiscoveryService Discovery() => new(
        _metadata,
        global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()),
        Options.Create(_options),
        CatalogVisibilityTestFactory.Create(_metadata, _consents, _options));

    private McpDatasetCatalog Catalog() => new(
        _metadata,
        _consents,
        Substitute.For<IGatewayExecutionService>(),
        options: Options.Create(_options),
        visibilityService: CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents, _options));

    private static McpToolDefinition Tool(string name) => new(name, name, "{}", TargetGraphQLOperation: name);

    private static CatalogSearchHit Hit(TableIdentifier id, IReadOnlyList<TableRelationship>? relations = null, string? kind = null, string? datasetId = null) => new(
        TableIdentifier: id,
        DisplayName: id.TableName,
        Description: null,
        Domain: id.Domain,
        Sensitivity: "NORMAL",
        CombinedScore: 0.9,
        Bm25Score: 0.9,
        VectorScore: null,
        MatchedTerms: [],
        RelevantColumns: [],
        RelatedJoinPaths: relations ?? [],
        SuggestedGraphQlField: null,
        Kind: kind ?? "table",
        DatasetId: datasetId);

    private async Task<IReadOnlyList<string>> ListedIdsAsync()
    {
        var listed = await Catalog().ListDatasetsAsync(User(), search: null, domain: null, offset: 0, limit: 50, tag: null, language: null, overview: false, format: "compact", ct: default);
        return listed.Datasets.Select(d => d.Id).ToList();
    }

    [Fact]
    public async Task SearchCatalog_SameTableNameInTwoSchemas_EachHitDescribesItsOwnTable()
    {
        var json = await Handler(Discovery()).ExecuteToolAsync(Tool(McpDatasetTools.SearchCatalog), """{"query":"telematic_unit","limit":10}""", Session);

        var hitIds = JsonDocument.Parse(json).RootElement.GetProperty("datasets").EnumerateArray()
            .Select(h => h.GetProperty("table").GetString()!)
            .ToList();

        hitIds.ShouldBe(["fleet.live.telematic_unit", "fleet.archive.telematic_unit"], ignoreOrder: true);
        var listed = await ListedIdsAsync();
        foreach (var id in hitIds)
        {
            listed.ShouldContain(id);
            var described = await Catalog().DescribeDatasetAsync(User(), id, language: null, brief: true, ct: default);
            described.Id.ShouldBe(id);
        }
    }

    [Fact]
    public async Task FindRelevantTables_SameTableNameInTwoSchemas_EachCandidateDescribesItsOwnTable()
    {
        var json = await Handler(Discovery()).ExecuteToolAsync(Tool(McpDatasetTools.FindRelevantTables), """{"use_case":"telematic_unit","limit":10}""", Session);

        var result = JsonSerializer.Deserialize<McpUseCaseDiscoveryResult>(json, JsonOpts)!;
        var ids = result.Candidates.Select(c => c.Dataset).ToList();

        ids.ShouldBe(["fleet.live.telematic_unit", "fleet.archive.telematic_unit"], ignoreOrder: true);
        var listed = await ListedIdsAsync();
        foreach (var id in ids)
        {
            listed.ShouldContain(id);
            (await Catalog().DescribeDatasetAsync(User(), id, language: null, brief: true, ct: default)).Id.ShouldBe(id);
        }
    }

    [Fact]
    public async Task SearchCatalog_JoinTarget_UsesFullIdentifier_JoinConditionKeepsTableNames()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>(
                [Hit(LiveUnit, [new TableRelationship(LiveUnit, "crane_id", Crane, "id", TableRelationshipType.ForeignKey)])]));

        var json = await Handler(discovery).ExecuteToolAsync(Tool(McpDatasetTools.SearchCatalog), """{"query":"unit"}""", Session);

        var hit = JsonDocument.Parse(json).RootElement.GetProperty("datasets")[0];
        hit.GetProperty("table").GetString().ShouldBe("fleet.live.telematic_unit");
        var join = hit.GetProperty("join_relations")[0];
        join.GetProperty("target_table").GetString().ShouldBe("fleet.live.crane");
        join.GetProperty("join_condition").GetString().ShouldBe("telematic_unit.crane_id = crane.id");
    }

    [Fact]
    public async Task FindRelevantTables_HitWithoutDatasetId_AndJoinTarget_UseFullIdentifier()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>(
                [Hit(ArchiveUnit, [new TableRelationship(ArchiveUnit, "crane_id", Crane, "id", TableRelationshipType.ForeignKey)])]));

        var json = await Handler(discovery).ExecuteToolAsync(Tool(McpDatasetTools.FindRelevantTables), """{"use_case":"unit"}""", Session);

        var candidate = JsonSerializer.Deserialize<McpUseCaseDiscoveryResult>(json, JsonOpts)!.Candidates.ShouldHaveSingleItem();
        candidate.Dataset.ShouldBe("fleet.archive.telematic_unit");
        candidate.Relations.ShouldHaveSingleItem().TargetTable.ShouldBe("fleet.live.crane");
    }

    [Fact]
    public async Task SearchCatalog_ProcedureHit_KeepsTheProcedureId()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>(
                [Hit(new TableIdentifier("fleet", "procedure", "crane_usage"), kind: "procedure", datasetId: "procedure:crane_usage")]));

        var search = await Handler(discovery).ExecuteToolAsync(Tool(McpDatasetTools.SearchCatalog), """{"query":"usage"}""", Session);
        var find = await Handler(discovery).ExecuteToolAsync(Tool(McpDatasetTools.FindRelevantTables), """{"use_case":"usage"}""", Session);

        JsonDocument.Parse(search).RootElement.GetProperty("datasets")[0].GetProperty("table").GetString().ShouldBe("procedure:crane_usage");
        JsonSerializer.Deserialize<McpUseCaseDiscoveryResult>(find, JsonOpts)!.Candidates.ShouldHaveSingleItem().Dataset.ShouldBe("procedure:crane_usage");
    }
}
