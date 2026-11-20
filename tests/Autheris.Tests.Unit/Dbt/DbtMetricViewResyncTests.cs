namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtMetricViewResyncTests
{
    private static GatewayOptions DatabricksOptions()
    {
        var opts = new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = true, ActivateNewTables = true }
        };
        opts.DataSources.Connections["dbx"] = new DataSourceConnectionOptions
        {
            Provider = "Databricks",
            ConnectionString = "Host=example"
        };
        return opts;
    }

    private static DbtMetadataIngestionService CreateService(ITableMetadataRepository repo) =>
        new(
            proposalRepository: new InMemoryDbtProposalRepository(),
            metadataRepository: repo,
            lineageGraphStore: Substitute.For<ILineageGraphStore>(),
            epochRepository: null,
            gatewayOptions: Options.Create(DatabricksOptions()),
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            tagImport: null,
            artifactStore: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance);

    private static string BuildSourceManifest(
        string objectKind = "metric_view",
        string? countMeasure = "row_count",
        string? revenueRole = "measure",
        string? regionDesc = "Updated region description",
        bool asModel = false)
    {
        var meta = new Dictionary<string, object?> { ["autheris.object_kind"] = objectKind };
        if (countMeasure is not null)
        {
            meta["autheris.count_measure"] = countMeasure;
        }

        var regionCol = new Dictionary<string, object?>
        {
            ["name"] = "region",
            ["data_type"] = "string",
            ["description"] = regionDesc,
            ["meta"] = new Dictionary<string, object?> { ["autheris.column_role"] = "dimension" }
        };
        var revenueCol = new Dictionary<string, object?>
        {
            ["name"] = "revenue",
            ["data_type"] = "decimal(18,2)",
            ["meta"] = revenueRole is not null
                ? new Dictionary<string, object?> { ["autheris.column_role"] = revenueRole }
                : new Dictionary<string, object?>()
        };

        var columns = new Dictionary<string, object?>
        {
            ["region"] = regionCol,
            ["revenue"] = revenueCol
        };

        var nodes = new Dictionary<string, object?>();
        var sources = new Dictionary<string, object?>();
        if (asModel)
        {
            nodes["model.proj.sales_mv"] = new
            {
                name = "sales_mv",
                database = "dbx",
                schema = "analytics",
                description = "Sales model",
                config = new { materialized = "table" },
                tags = Array.Empty<string>(),
                meta,
                columns,
                depends_on = new { nodes = Array.Empty<string>() }
            };
        }
        else
        {
            sources["source.proj.dbx.sales_mv"] = new
            {
                name = "sales_mv",
                source_name = "dbx",
                database = "dbx",
                schema = "analytics",
                description = "Sales metric view",
                tags = Array.Empty<string>(),
                meta,
                columns
            };
        }

        return JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes, sources });
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestResync_PreservesCountMeasureRolesDiscoveryHiddenAndLocalization()
    {
        var existingId = Guid.NewGuid();
        var storedTexts = new LocalizedTexts(ImmutableDictionary<string, LocalizedTextEntry>.Empty.Add("en", new LocalizedTextEntry("Region EN", "Stored region description")));
        var stored = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            Table = new Table
            {
                Id = existingId,
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                DisplayName = "sales_mv",
                DbtUniqueId = "source.proj.dbx.sales_mv",
                ObjectKind = DiscoveryObjectKind.MetricView,
                MetricViewCountMeasure = "row_count",
                IsActive = true
            },
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    TableId = existingId,
                    ColumnName = "region",
                    DataType = "string",
                    DisplayName = "Sales Region",
                    Collation = "UTF8_BINARY",
                    Texts = storedTexts,
                    DocumentationSource = "Manual",
                    DiscoveryHidden = false,
                    Role = MetricViewColumnRole.Dimension
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    TableId = existingId,
                    ColumnName = "revenue",
                    DataType = "decimal(18,2)",
                    DiscoveryHidden = false,
                    Role = MetricViewColumnRole.Measure
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    TableId = existingId,
                    ColumnName = "internal_col",
                    DataType = "string",
                    DiscoveryHidden = true,
                    Role = null
                }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(stored.Identifier, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(stored));
        TableMetadata? persisted = null;
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                persisted = ci.Arg<TableMetadata>();
                return Task.FromResult(persisted);
            });

        var svc = CreateService(repo);
        // Re-sync omits count_measure in manifest to verify stored value is preserved alongside role & column metadata.
        var manifest = BuildSourceManifest(objectKind: "metric_view", countMeasure: null, revenueRole: "measure", regionDesc: "Refreshed desc");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.Table.ObjectKind.ShouldBe(DiscoveryObjectKind.MetricView);
        persisted.Table.MetricViewCountMeasure.ShouldBe("row_count");

        var region = persisted.Columns.Single(c => c.ColumnName == "region");
        region.Role.ShouldBe(MetricViewColumnRole.Dimension);
        region.DiscoveryHidden.ShouldBeFalse();
        region.DisplayName.ShouldBe("Sales Region");
        region.Collation.ShouldBe("UTF8_BINARY");
        region.Texts.Entries["en"].DisplayName.ShouldBe("Region EN");
        region.DocumentationSource.ShouldBe("Manual");
        region.Description.ShouldBe("Refreshed desc");

        var internalCol = persisted.Columns.Single(c => c.ColumnName == "internal_col");
        internalCol.DiscoveryHidden.ShouldBeTrue();
        internalCol.Role.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ManifestResync_ModelClaimingMetricViewOrColumnRoleOnTable_IsRejected()
    {
        var existingId = Guid.NewGuid();
        var stored = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            Table = new Table
            {
                Id = existingId,
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                DbtUniqueId = "model.proj.sales_mv",
                ObjectKind = DiscoveryObjectKind.Table,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = existingId, ColumnName = "region", DataType = "string" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(stored.Identifier, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(stored));
        var svc = CreateService(repo);

        // Model declaring metric_view must be rejected even on update.
        using (var s1 = new MemoryStream(Encoding.UTF8.GetBytes(BuildSourceManifest(objectKind: "metric_view", asModel: true))))
        {
            var r1 = await svc.IngestManifestStreamAsClusterAdminAsync(s1, dryRun: false);
            r1.RejectedModels.ShouldContain("sales_mv");
            r1.Warnings.ShouldNotBeEmpty();
            await repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        }

        // Source of kind table declaring column_role must also be rejected on update.
        var tableStored = stored with { Table = stored.Table with { DbtUniqueId = "source.proj.dbx.sales_mv" } };
        repo.GetTableMetadataAsync(stored.Identifier, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(tableStored));
        using (var s2 = new MemoryStream(Encoding.UTF8.GetBytes(BuildSourceManifest(objectKind: "table", countMeasure: null, revenueRole: "measure", asModel: false))))
        {
            var r2 = await svc.IngestManifestStreamAsClusterAdminAsync(s2, dryRun: false);
            r2.RejectedModels.ShouldContain("sales_mv");
            r2.Warnings.ShouldNotBeEmpty();
            await repo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GovernanceImport_PreservesMetricViewCountMeasureAndColumnRolesAndTexts()
    {
        var existingId = Guid.NewGuid();
        var texts = new LocalizedTexts(ImmutableDictionary<string, LocalizedTextEntry>.Empty.Add("en", new LocalizedTextEntry("Revenue EN", "Total sales")));
        var stored = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            Table = new Table
            {
                Id = existingId,
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                ObjectKind = DiscoveryObjectKind.MetricView,
                MetricViewCountMeasure = "row_count",
                IsActive = true
            },
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    TableId = existingId,
                    ColumnName = "revenue",
                    DataType = "decimal(18,2)",
                    DisplayName = "Total Revenue",
                    Collation = "UTF8_BINARY",
                    Texts = texts,
                    DocumentationSource = "Manual",
                    DiscoveryHidden = false,
                    Role = MetricViewColumnRole.Measure
                }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(stored.Identifier, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(stored));
        TableMetadata? persisted = null;
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                persisted = ci.Arg<TableMetadata>();
                return Task.FromResult(persisted);
            });

        var svc = CreateService(repo);
        var govJson = JsonSerializer.Serialize(new
        {
            classifications = new[]
            {
                new
                {
                    table = "dbx.analytics.sales_mv",
                    description = "Curated mv description",
                    columns = new Dictionary<string, object?>
                    {
                        ["revenue"] = new { description = "Revenue description" }
                    }
                }
            }
        });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(govJson));
        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-ADMIN"), IsSync: false, null),
            new TenantId("default"),
            IsClusterAdmin: true);

        var res = await svc.IngestGovernanceStreamAsync(stream, caller, dryRun: false);
        res.Success.ShouldBeTrue();
        persisted.ShouldNotBeNull();
        persisted.Table.ObjectKind.ShouldBe(DiscoveryObjectKind.MetricView);
        persisted.Table.MetricViewCountMeasure.ShouldBe("row_count");
        var rev = persisted.Columns.Single(c => c.ColumnName == "revenue");
        rev.Role.ShouldBe(MetricViewColumnRole.Measure);
        rev.DisplayName.ShouldBe("Total Revenue");
        rev.Collation.ShouldBe("UTF8_BINARY");
        rev.Texts.Entries["en"].DisplayName.ShouldBe("Revenue EN");
        rev.DocumentationSource.ShouldBe("Manual");
    }
}
