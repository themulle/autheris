namespace Autheris.Tests.Unit.Catalog;

using System.Text;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-6 (A-2): dbt model tags are imported as dbt-source dataset tags; the ingestion upsert itself writes no tags.</summary>
public sealed class DbtDatasetTagTests
{
    private const string Manifest = """
    {
      "nodes": {
        "model.proj.orders": {
          "name": "orders", "database": "db", "schema": "dbo", "description": "orders",
          "config": { "materialized": "table" },
          "tags": ["PII", "finance"],
          "columns": { "id": { "name": "id", "data_type": "int", "meta": {}, "tags": ["column-only-tag"] } },
          "depends_on": { "nodes": [] }
        }
      }
    }
    """;

    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly IDatasetTagImportService _tags = Substitute.For<IDatasetTagImportService>();
    private readonly List<string> _order = [];

    public DbtDatasetTagTests()
    {
        var id = new TableIdentifier("db", "dbo", "orders");
        _repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "db", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSQL", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id" }]
        });
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(c => { _order.Add("upsert"); return Task.FromResult(c.Arg<TableMetadata>()); });
        _tags.ImportDbtTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>())
            .Returns(c => { _order.Add("tags"); return Task.FromResult(DatasetTagImportResult.Unchanged); });
    }

    private DbtMetadataIngestionService Service() => new(Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(),
        null, null, null, null, null, null, _tags, NullLogger<DbtMetadataIngestionService>.Instance);

    [Fact]
    public async Task ModelTags_AreImportedAfterTheUpsert_ColumnTagsAreNot()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest));
        await Service().IngestManifestStreamAsync(stream, dryRun: false);

        _order.ShouldBe(["upsert", "tags"]);
        await _tags.Received(1).ImportDbtTagsAsync(new TableIdentifier("db", "dbo", "orders"),
            Arg.Is<IEnumerable<string?>>(t => t.SequenceEqual(new string?[] { "PII", "finance" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRun_ImportsNothing()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest));
        await Service().IngestManifestStreamAsync(stream, dryRun: true);

        _tags.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ImportFailure_DoesNotFailTheIngestion_ButIsReportedAsWarning()
    {
        _tags.ImportDbtTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetTagImportResult(false, 1, 2, Failed: true));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest));

        var result = await Service().IngestManifestStreamAsync(stream, dryRun: false);

        result.Warnings.ShouldContain(w => w.Contains("'db.dbo.orders'") && w.Contains("tags"));
    }

    [Fact]
    public async Task ImportCounts_AreReportedAsWarningsWithoutTagValues()
    {
        _tags.ImportDbtTagsAsync(Arg.Any<TableIdentifier>(), Arg.Any<IEnumerable<string?>>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetTagImportResult(true, 1, 2, false));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Manifest));

        var result = await Service().IngestManifestStreamAsync(stream, dryRun: false);

        result.Warnings.ShouldContain(w => w.Contains("1 invalid") && w.Contains("2 over the cap"));
        result.Warnings.ShouldNotContain(w => w.Contains("PII"));
    }

    [Fact]
    public async Task SemanticModels_GetTheSyntheticTagsThroughTheImport_NotThroughTheUpsertLiteral()
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        TableMetadata? captured = null;
        tableRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => captured = m), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<TableMetadata>()));
        var service = new DbtSemanticManifestIngestionService(
            Substitute.For<ISemanticCatalogService>(), NullLogger<DbtSemanticManifestIngestionService>.Instance, tableRepo, _tags);

        await service.ParseAndIngestAsync("""
        { "semantic_models": [ { "name": "orders", "description": "d", "node_relation": { "relation_name": "a.b.orders" },
            "entities": [ { "name": "order_id", "type": "primary" } ], "dimensions": [], "measures": [] } ], "metrics": [] }
        """);

        captured!.Table.Tags.ShouldBeEmpty();
        await _tags.Received(1).ImportDbtTagsAsync(new TableIdentifier("semantic", "semantic", "orders"),
            Arg.Is<IEnumerable<string?>>(t => t.SequenceEqual(new string?[] { "dbt", "semantic-model" })), Arg.Any<CancellationToken>());
    }
}
