namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public class DbtManifestGovernanceTests
{
    [Fact]
    public async Task IngestManifest_PreservesExistingRequiredFilterColumns_WhenIncomingHasNone()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, NullLogger<DbtMetadataIngestionService>.Instance);

        var tableId = new TableIdentifier("tenant_x", "analytics", "orders");
        var existingMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "tenant_x",
                SchemaName = "analytics",
                TableName = "orders",
                RequiredFilterColumns = ["tenant_id", "region_code"]
            },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "integer" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingMetadata);

        const string manifestJson = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.analytics.orders": {
              "name": "orders",
              "database": "tenant_x",
              "schema": "analytics",
              "description": "Orders model",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {},
              "columns": {
                "order_id": { "name": "order_id", "data_type": "integer", "tags": [], "meta": {} }
              }
            }
          }
        }
        """;

        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-1-USER"), IsSync: false),
            new TenantId("tenant_x"));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        var result = await service.IngestManifestStreamAsync(stream, caller);

        result.Success.ShouldBeTrue();
        await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m =>
            m.Table.RequiredFilterColumns.Length == 2 &&
            m.Table.RequiredFilterColumns.Contains("tenant_id") &&
            m.Table.RequiredFilterColumns.Contains("region_code")
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestManifest_UnionsRequiredFilterColumns_WhenIncomingHasNewColumn()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, NullLogger<DbtMetadataIngestionService>.Instance);

        var tableId = new TableIdentifier("tenant_x", "analytics", "orders");
        var existingMetadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "tenant_x",
                SchemaName = "analytics",
                TableName = "orders",
                RequiredFilterColumns = ["tenant_id"]
            },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "integer" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "created_date", DataType = "date" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingMetadata);

        const string manifestJson = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.analytics.orders": {
              "name": "orders",
              "database": "tenant_x",
              "schema": "analytics",
              "description": "Orders model",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {
                "required_filter_columns": ["created_date"]
              },
              "columns": {
                "order_id": { "name": "order_id", "data_type": "integer", "tags": [], "meta": {} }
              }
            }
          }
        }
        """;

        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-1-USER"), IsSync: false),
            new TenantId("tenant_x"));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        var result = await service.IngestManifestStreamAsync(stream, caller);

        result.Success.ShouldBeTrue();
        await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m =>
            m.Table.RequiredFilterColumns.Length == 2 &&
            m.Table.RequiredFilterColumns.Contains("tenant_id") &&
            m.Table.RequiredFilterColumns.Contains("created_date")
        ), Arg.Any<CancellationToken>());
    }
}
