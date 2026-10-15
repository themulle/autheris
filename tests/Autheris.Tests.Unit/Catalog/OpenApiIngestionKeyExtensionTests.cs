namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (D-8, SEC-94-11): keys come only from x-autheris-primary-key / x-autheris-indexes; no key is guessed.</summary>
public sealed class OpenApiIngestionKeyExtensionTests
{
    private static string Spec(string schemaExtras, string required = "") => $$"""
    {
      "openapi": "3.0.0",
      "info": { "title": "Svc", "version": "1" },
      "servers": [ { "url": "https://svc.example.com" } ],
      "paths": { "/orders": { "get": { "summary": "list" } } },
      "components": { "schemas": { "Order": {
        "type": "object",
        {{required}}
        {{schemaExtras}}
        "properties": {
          "id": { "type": "integer" },
          "tenant_id": { "type": "integer" },
          "sku": { "type": "string" }
        }
      } } }
    }
    """;

    private static async Task<(TableMetadata Meta, IReadOnlyList<string> Warnings)> IngestAsync(string json)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var upserts = new List<TableMetadata>();
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(upserts.Add), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(c.Arg<TableMetadata>()));
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        var result = await service.IngestOpenApiJsonAsync(json, domain: "svc");
        result.Success.ShouldBeTrue();
        return (upserts.Single(), result.Warnings);
    }

    [Fact]
    public async Task DeclaredExtensions_BecomeCompositeKeyAndIndexes()
    {
        var (meta, _) = await IngestAsync(Spec("""
            "x-autheris-primary-key": ["tenant_id", "id"],
            "x-autheris-indexes": [ { "name": "ux_sku", "columns": ["sku"], "unique": true }, { "name": "ix_tenant_sku", "columns": ["tenant_id", "sku"] } ],
            """));

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["tenant_id", "id"]);
        meta.Indexes.Count.ShouldBe(2);
        meta.Indexes[0].IsUnique.ShouldBeTrue();
        meta.Indexes[1].IsUnique.ShouldBeFalse();
    }

    [Fact]
    public async Task NoExtensions_NoKeyIsGuessed_NotFromRequired_NotFromIdColumns_NotFromFirstColumn()
    {
        var (meta, _) = await IngestAsync(Spec(string.Empty, required: "\"required\": [\"sku\"],"));

        meta.PrimaryKeyColumns.ShouldBeEmpty();
        meta.Indexes.ShouldBeEmpty();
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"x-autheris-primary-key\": \"id\",")]
    [InlineData("\"x-autheris-primary-key\": [1, 2],")]
    [InlineData("\"x-autheris-primary-key\": [\"id\", \"nope\"],")]
    [InlineData("\"x-autheris-primary-key\": [\"id\", \"id\"],")]
    [InlineData("\"x-autheris-indexes\": {},")]
    [InlineData("\"x-autheris-indexes\": [ { \"columns\": [\"sku\"] } ],")]
    [InlineData("\"x-autheris-indexes\": [ { \"name\": \"ix\", \"columns\": [\"sku\"], \"unique\": \"yes\" } ],")]
    [InlineData("\"x-autheris-indexes\": [ { \"name\": \"<b>x</b>\", \"columns\": [\"sku\"] } ],")]
    [InlineData("\"x-autheris-indexes\": [ { \"name\": \"ix\", \"columns\": [] } ],")]
    [InlineData("\"x-autheris-indexes\": [ 5 ],")]
    public async Task MalformedExtension_DefinitionDropped_TableIngested(string extras)
    {
        var (meta, _) = await IngestAsync(Spec(extras));

        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
        meta.Indexes.ShouldBeEmpty();
        meta.Columns.Count.ShouldBe(3);
    }

    [Fact]
    public async Task OversizedArrays_DefinitionDropped()
    {
        var hugeColumns = string.Join(",", Enumerable.Range(0, 10_000).Select(i => $"\"c{i}\""));
        var (meta, _) = await IngestAsync(Spec($"\"x-autheris-primary-key\": [{hugeColumns}],"));
        meta.ReplaceKeyDefinition.ShouldBeFalse();

        var hugeIndexes = string.Join(",", Enumerable.Range(0, 200).Select(i => $"{{\"name\":\"ix_{i}\",\"columns\":[\"sku\"]}}"));
        (meta, _) = await IngestAsync(Spec($"\"x-autheris-indexes\": [{hugeIndexes}],"));
        meta.ReplaceKeyDefinition.ShouldBeFalse();
    }

    [Fact]
    public async Task ExistingTable_KeyOnIgnoredPhantomColumn_IsDropped_NotAnError()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var id = new TableIdentifier("svc", "api", "order");
        repo.GetTableMetadataAsync(id, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SchemaName = "api", TableName = "order", IsActive = true, DataSourceType = DataSourceType.HttpDeclarative },
            Columns = [new TableColumn { ColumnName = "id" }],
            PrimaryKeyColumns = ["id"]
        });
        TableMetadata? saved = null;
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => saved = m), Arg.Any<CancellationToken>()).Returns(c => Task.FromResult(c.Arg<TableMetadata>()));
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);

        await service.IngestOpenApiJsonAsync(Spec("\"x-autheris-primary-key\": [\"tenant_id\", \"id\"],"), domain: "svc");

        saved.ShouldNotBeNull();
        saved!.ReplaceKeyDefinition.ShouldBeFalse();
        saved.PrimaryKeyColumns.ShouldBe(["id"]);
    }
}
