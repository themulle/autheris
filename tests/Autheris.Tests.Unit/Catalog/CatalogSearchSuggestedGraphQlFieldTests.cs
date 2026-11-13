namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #682 A: the GraphQL field a search hit suggests is the table's real root field from the catalog GraphQL schema
/// (<c>domain_schema_table</c>, sanitized), never a string formula; null when the table has no root field.
/// </summary>
public sealed class CatalogSearchSuggestedGraphQlFieldTests
{
    // Underscores and a character the GraphQL name sanitizer replaces, in a SQL table (part of the GraphQL schema).
    private static readonly TableIdentifier Gps = new("fleet-ops", "raw_v2", "gps_position");
    // A lakehouse table is not part of the GraphQL catalog schema.
    private static readonly TableIdentifier GpsFiles = new("fleet-ops", "lake", "gps_position_files");

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly ICatalogSearchEngine _engine = Substitute.For<ICatalogSearchEngine>();
    private readonly GatewayOptions _options = new()
    {
        Catalog = new DataCatalogOptions { SharedTables = ["fleet-ops.raw_v2.gps_position", "fleet-ops.lake.gps_position_files"] }
    };

    public CatalogSearchSuggestedGraphQlFieldTests()
    {
        IReadOnlyList<TableMetadata> tables = [Table(Gps, DataSourceType.Sql), Table(GpsFiles, DataSourceType.LakehouseIceberg)];
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tables));
    }

    private static TableMetadata Table(TableIdentifier id, DataSourceType type) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = true, SourceType = "PostgreSql", DataSourceType = type, Description = "GPS positions" },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static RequestContext Context() => Wp0.Context(Wp0.User("S-1-USER", "tenant-a", ["GovernanceAdmin"]));

    private CatalogDiscoveryService Service(bool indexReady)
    {
        _engine.IsIndexReady.Returns(indexReady);
        return new CatalogDiscoveryService(
            _metadata,
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>()),
            Options.Create(_options),
            CatalogVisibilityTestFactory.Create(_metadata, Substitute.For<IConsentRepository>(), _options),
            searchEngine: _engine,
            graphQlMap: new CatalogGraphQlMap(_metadata, Substitute.For<ITableRelationRepository>()));
    }

    private static CatalogSearchHit EngineHit(TableIdentifier id) => new(
        TableIdentifier: id,
        DisplayName: id.TableName,
        Description: "GPS positions",
        Domain: id.Domain,
        Sensitivity: "NORMAL",
        CombinedScore: 0.9,
        Bm25Score: 0.9,
        VectorScore: null,
        MatchedTerms: ["gps"],
        RelevantColumns: [],
        RelatedJoinPaths: [],
        SuggestedGraphQlField: null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuggestedGraphQlField_EqualsSchemaRootField_OrNullWithoutRootField(bool indexReady)
    {
        _engine.SearchAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<Func<TableIdentifier, bool>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>([EngineHit(Gps), EngineHit(GpsFiles)]));

        var hits = await Service(indexReady).SearchCatalogDetailedAsync(new CatalogSearchQuery("gps_position", null, 10, CatalogSearchMode.Hybrid), Context());

        hits.ShouldContain(h => h.TableIdentifier == Gps && h.SuggestedGraphQlField == "fleet_ops_raw_v2_gps_position");
        hits.ShouldContain(h => h.TableIdentifier == GpsFiles && h.SuggestedGraphQlField == null);
        hits.Count.ShouldBe(2);
    }
}
