namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DbtCatalogMinGroupSizeIngestionTests
{
    private static readonly TableIdentifier TargetTable = new("postgres", "public", "stg_orders");

    private static TableMetadata CreateStoredMetadata(
        int? tableMinGroup = null,
        bool tableNonPersonal = false,
        int? overlayMinGroup = null,
        bool? overlayNonPersonal = null)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = TargetTable,
            MinGroupSize = overlayMinGroup,
            NonPersonal = overlayNonPersonal,
            Table = new Table
            {
                Id = tableId,
                SourceType = "PostgreSQL",
                SourceName = "postgres",
                SchemaName = "public",
                TableName = "stg_orders",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                MinGroupSize = tableMinGroup,
                NonPersonal = tableNonPersonal
            },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = tableId, ColumnName = "order_date", DataType = "date" }
            ]
        };
    }

    private static string BuildManifestJson(string metaJson, string? columnMetaExtra = null)
    {
        var colExtra = columnMetaExtra ?? "";
        return $$"""
        {
          "metadata": {
            "dbt_version": "1.8.0",
            "project_name": "test_corp"
          },
          "nodes": {
            "model.test_corp.stg_orders": {
              "name": "stg_orders",
              "database": "postgres",
              "schema": "public",
              "description": "Orders staging table",
              "config": { "materialized": "table" },
              "tags": [],
              "meta": {{metaJson}},
              "columns": {
                "id": {
                  "name": "id",
                  "data_type": "integer",
                  "description": "Primary key",
                  "tags": [],
                  "meta": {}
                },
                "order_date": {
                  "name": "order_date",
                  "data_type": "date",
                  "description": "Date of order",
                  "tags": [],
                  "meta": {}
                }{{colExtra}}
              }
            }
          }
        }
        """;
    }

    private static (DbtMetadataIngestionService Service, ITableMetadataRepository Repo, IAuditWriter AuditWriter, Func<TableMetadata?> GetPersisted) CreateHarness(
        TableMetadata stored,
        bool withAuditWriter = true)
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var auditWriter = withAuditWriter ? Substitute.For<IAuditWriter>() : null;
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        metadataRepo.GetTableMetadataAsync(TargetTable, Arg.Any<CancellationToken>()).Returns(stored);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([stored]);

        TableMetadata? persisted = null;
        metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                persisted = ci.Arg<TableMetadata>();
                return persisted;
            });

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-a" }
        });

        var service = new DbtMetadataIngestionService(
            proposalRepo,
            metadataRepo,
            graphStore,
            null,
            gatewayOptions,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            auditWriter,
            logger);

        return (service, metadataRepo, auditWriter!, () => persisted);
    }

    [Fact]
    public async Task Ingest_FlatKeys_SetsTableValues()
    {
        var stored = CreateStoredMetadata();
        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "5",
          "autheris.non_personal": "true"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBe(5);
        persisted.Table.NonPersonal.ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_NestedAutherisObject_SetsTableValues()
    {
        var stored = CreateStoredMetadata();
        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var json = BuildManifestJson("""
        {
          "autheris": {
            "min_group_size": 10,
            "non_personal": true
          }
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBe(10);
        persisted.Table.NonPersonal.ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_KeyRemoved_ClearsValues()
    {
        var stored = CreateStoredMetadata(tableMinGroup: 5, tableNonPersonal: true);
        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var json = BuildManifestJson("{}");

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBeNull();
        persisted.Table.NonPersonal.ShouldBeFalse();
    }

    [Fact]
    public async Task Ingest_InvalidValue_WarnsAndClears()
    {
        var stored = CreateStoredMetadata(tableMinGroup: 5, tableNonPersonal: true);
        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "none",
          "autheris.non_personal": "not_a_bool"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBeNull();
        persisted.Table.NonPersonal.ShouldBeFalse();
        result.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Ingest_PiiColumn_ForcesNonPersonalFalse()
    {
        var stored = CreateStoredMetadata();
        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var colExtra = """
            ,
                    "customer_email": {
                      "name": "customer_email",
                      "data_type": "varchar",
                      "description": "Email address",
                      "tags": ["email"],
                      "meta": {}
                    }
            """;

        var json = BuildManifestJson("""
        {
          "autheris.non_personal": "true"
        }
        """, colExtra);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.NonPersonal.ShouldBeFalse();
        result.Warnings.Any(w => w.Contains("personal data") || w.Contains("non_personal")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_Change_AuditedWithSourceDbt()
    {
        var stored = CreateStoredMetadata(tableMinGroup: 3, tableNonPersonal: false);
        var (svc, _, auditWriter, _) = CreateHarness(stored, withAuditWriter: true);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "5",
          "autheris.non_personal": "true"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        await auditWriter.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogMinGroupSizeChanged &&
            e.DetailsJson.Contains("\"source\":\"dbt\"")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ingest_NoAuditWriter_KeepsStoredValues()
    {
        var stored = CreateStoredMetadata(tableMinGroup: 3, tableNonPersonal: false);
        var (svc, _, _, getPersisted) = CreateHarness(stored, withAuditWriter: false);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "5",
          "autheris.non_personal": "true"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        // Stored values kept because changes cannot be audited!
        persisted.Table.MinGroupSize.ShouldBe(3);
        persisted.Table.NonPersonal.ShouldBeFalse();
        result.Warnings.Any(w => w.Contains("could not be audited")).ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_GovernanceOverlay_StillWins()
    {
        // Governance overlay has MinGroupSize = 10, NonPersonal = true
        var stored = CreateStoredMetadata(
            tableMinGroup: 2,
            tableNonPersonal: false,
            overlayMinGroup: 10,
            overlayNonPersonal: true);

        var (svc, _, _, getPersisted) = CreateHarness(stored);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "5",
          "autheris.non_personal": "false"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        var persisted = getPersisted();
        persisted.ShouldNotBeNull();
        persisted.Table.MinGroupSize.ShouldBe(5);
        persisted.Table.NonPersonal.ShouldBeFalse();
        // Overlay wins on EffectiveMinGroupSize & EffectiveNonPersonal!
        persisted.EffectiveMinGroupSize.ShouldBe(10);
        persisted.EffectiveNonPersonal.ShouldBeTrue();
    }

    [Fact]
    public async Task Ingest_DryRun_NoWrite()
    {
        var stored = CreateStoredMetadata();
        var (svc, repo, auditWriter, getPersisted) = CreateHarness(stored, withAuditWriter: true);

        var json = BuildManifestJson("""
        {
          "autheris.min_group_size": "5",
          "autheris.non_personal": "true"
        }
        """);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await svc.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: true);

        result.Success.ShouldBeTrue();
        getPersisted().ShouldBeNull();
        await repo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!, default);
        await auditWriter.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }
}
