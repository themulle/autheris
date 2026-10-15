namespace Autheris.Tests.Unit.Catalog;

using System.Text;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (D-8, SEC-94-11): dbt primary_key / unique constraints and unique tests become the declared definition.</summary>
public sealed class DbtKeyConstraintTests
{
    private static string Manifest(string modelConstraints = "[]", string colId = "{}", string colTenant = "{}", string extraNodes = "") => $$"""
    {
      "nodes": {
        "model.proj.orders": {
          "name": "orders", "database": "db", "schema": "dbo", "description": "orders",
          "config": { "materialized": "table" },
          "constraints": {{modelConstraints}},
          "columns": {
            "tenant": { "name": "tenant", "data_type": "int", "meta": {}, "tags": [] {{(colTenant == "{}" ? "" : ", \"constraints\": " + colTenant)}} },
            "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] {{(colId == "{}" ? "" : ", \"constraints\": " + colId)}} },
            "email": { "name": "email", "data_type": "varchar", "meta": {}, "tags": [] }
          },
          "depends_on": { "nodes": [] }
        }
        {{extraNodes}}
      }
    }
    """;

    private static async Task<DbtModelDefinition> ParseAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream)).Single(m => m.Name == "orders");
    }

    [Fact]
    public async Task ModelLevelPrimaryKeyAndUnique_AreParsed()
    {
        var model = await ParseAsync(Manifest(
            """[{"type":"primary_key","columns":["tenant","id"]},{"type":"unique","columns":["email"]},{"type":"foreign_key","columns":["id"],"to":"ref('x')","to_columns":["id"]}]"""));

        model.DeclaredKeys.ShouldNotBeNull();
        model.DeclaredKeys!.Invalid.ShouldBeFalse();
        model.DeclaredKeys.PrimaryKey.ShouldBe(["tenant", "id"]);
        model.DeclaredKeys.UniqueConstraints.Single().ShouldBe(["email"]);
    }

    [Fact]
    public async Task ColumnLevelPrimaryKey_CollectsInColumnOrder()
    {
        var model = await ParseAsync(Manifest(colTenant: """[{"type":"primary_key"}]""", colId: """[{"type":"primary_key"},{"type":"unique"}]"""));

        model.DeclaredKeys!.PrimaryKey.ShouldBe(["tenant", "id"]);
        model.DeclaredKeys.UniqueConstraints.Single().ShouldBe(["id"]);
    }

    [Fact]
    public async Task UniqueTest_AttachedToModel_BecomesUniqueConstraint()
    {
        var model = await ParseAsync(Manifest(extraNodes: """
            , "test.proj.unique_orders_email.1": {
              "attached_node": "model.proj.orders",
              "column_name": "email",
              "test_metadata": { "name": "unique", "kwargs": { "column_name": "email" } }
            },
            "test.proj.not_null_orders_id.2": {
              "attached_node": "model.proj.orders",
              "column_name": "id",
              "test_metadata": { "name": "not_null", "kwargs": { "column_name": "id" } }
            }
            """));

        model.DeclaredKeys!.PrimaryKey.ShouldBeNull();
        model.DeclaredKeys.UniqueConstraints.Single().ShouldBe(["email"]);
    }

    [Fact]
    public async Task NoConstraints_NoDeclaredKeys()
    {
        (await ParseAsync(Manifest())).DeclaredKeys.ShouldBeNull();
    }

    [Fact]
    public async Task TwoPrimaryKeys_Invalid()
    {
        var model = await ParseAsync(Manifest(
            modelConstraints: """[{"type":"primary_key","columns":["id"]}]""", colTenant: """[{"type":"primary_key"}]"""));
        model.DeclaredKeys!.Invalid.ShouldBeTrue();
    }

    [Fact]
    public async Task OversizedColumnArray_Invalid_WithoutEnumeratingIt()
    {
        var huge = string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"c{i}\""));
        var model = await ParseAsync(Manifest(modelConstraints: $$"""[{"type":"primary_key","columns":[{{huge}}]}]"""));
        model.DeclaredKeys!.Invalid.ShouldBeTrue();
    }

    [Fact]
    public async Task NonStringColumnEntries_Invalid()
    {
        var model = await ParseAsync(Manifest(modelConstraints: """[{"type":"primary_key","columns":[1,{"x":2}]}]"""));
        model.DeclaredKeys!.Invalid.ShouldBeTrue();
    }

    // ---- ingestion service

    private readonly ITableMetadataRepository _repo = Substitute.For<ITableMetadataRepository>();
    private readonly List<TableMetadata> _upserts = new();

    private async Task<TableMetadata> IngestAsync(string manifest)
    {
        var id = new TableIdentifier("db", "dbo", "orders");
        _repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = "db", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSQL", IsActive = true },
            Columns = [new TableColumn { ColumnName = "tenant" }, new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "email" }],
            PrimaryKeyColumns = ["id"]
        });
        _repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(c => { _upserts.Add(c.Arg<TableMetadata>()); return Task.FromResult(c.Arg<TableMetadata>()); });
        var service = new DbtMetadataIngestionService(Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(),
            NullLogger<DbtMetadataIngestionService>.Instance);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        await service.IngestManifestStreamAsync(stream, dryRun: false);
        return _upserts.Single();
    }

    [Fact]
    public async Task Ingest_DeclaredConstraints_ReplaceDefinition()
    {
        var meta = await IngestAsync(Manifest("""[{"type":"primary_key","columns":["tenant","id"]},{"type":"unique","columns":["email"]}]"""));

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["tenant", "id"]);
        meta.Indexes.Single().Name.ShouldBe("uq_1");
        meta.Indexes.Single().IsUnique.ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_NoConstraints_KeepsStoredDefinition()
    {
        var meta = await IngestAsync(Manifest());
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task UnknownColumn_DefinitionDropped()
    {
        var meta = await IngestAsync(Manifest("""[{"type":"primary_key","columns":["id","ghost"]}]"""));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.Columns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task InvalidDeclaration_DefinitionDropped_DocumentationStillSynced()
    {
        var meta = await IngestAsync(Manifest(
            modelConstraints: """[{"type":"primary_key","columns":["id"]}]""", colTenant: """[{"type":"primary_key"}]"""));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.Table.Description.ShouldBe("orders");
    }
}
