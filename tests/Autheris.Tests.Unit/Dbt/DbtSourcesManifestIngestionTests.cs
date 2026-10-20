namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtSourcesManifestIngestionTests
{
    private const string SourcesOnlyManifestJson = """
    {
      "metadata": {
        "dbt_version": "1.8.0",
        "project_name": "corp_data_platform"
      },
      "nodes": {},
      "sources": {
        "source.corp_data_platform.raw_lake.crm.contacts_raw": {
          "name": "contacts_raw",
          "database": "raw_lake",
          "schema": "crm",
          "description": "Raw ingested CRM contact records",
          "tags": ["external", "raw"],
          "meta": {
            "owner": "DataPlatform",
            "owner_email": "dataplatform@corp.local",
            "rls_filter": "tenant_id = @tenant_id"
          },
          "columns": {
            "contact_id": {
              "name": "contact_id",
              "data_type": "varchar",
              "description": "Primary identifier for contact",
              "tags": [],
              "meta": {}
            },
            "email": {
              "name": "email",
              "data_type": "varchar",
              "description": "Customer contact email address",
              "tags": ["pii"],
              "meta": {
                "pii": "true"
              }
            },
            "ssn_number": {
              "name": "ssn_number",
              "data_type": "varchar",
              "description": "Social security number",
              "tags": ["ssn"],
              "meta": {}
            }
          }
        }
      }
    }
    """;

    [Fact]
    public async Task Parser_ExtractsSourcesWithMaterializationSource()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SourcesOnlyManifestJson));
        var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);

        models.Count.ShouldBe(1);

        var sourceModel = models[0];
        sourceModel.UniqueId.ShouldBe("source.corp_data_platform.raw_lake.crm.contacts_raw");
        sourceModel.Name.ShouldBe("contacts_raw");
        sourceModel.Database.ShouldBe("raw_lake");
        sourceModel.Schema.ShouldBe("crm");
        sourceModel.Materialization.ShouldBe("source");
        sourceModel.Description.ShouldBe("Raw ingested CRM contact records");
        sourceModel.ContractEnforced.ShouldBeFalse();
        sourceModel.Tags.ShouldBe(["external", "raw"]);
        sourceModel.Meta["owner"].ShouldBe("DataPlatform");
        sourceModel.Meta["owner_email"].ShouldBe("dataplatform@corp.local");
        sourceModel.Meta["rls_filter"].ShouldBe("tenant_id = @tenant_id");

        sourceModel.Columns.Count.ShouldBe(3);
        var emailCol = sourceModel.Columns["email"];
        emailCol.DataType.ShouldBe("varchar");
        emailCol.Description.ShouldBe("Customer contact email address");
        emailCol.Tags.ShouldContain("pii");
        emailCol.Meta["pii"].ShouldBe("true");

        var ssnCol = sourceModel.Columns["ssn_number"];
        ssnCol.DataType.ShouldBe("varchar");
        ssnCol.Tags.ShouldContain("ssn");
    }

    [Fact]
    public async Task IngestionService_IngestSourcesOnlyManifest_EndToEnd_GeneratesProposalsAndUpdatesMetadata()
    {
        var proposalRepo = new InMemoryDbtProposalRepository();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var targetTableId = new TableIdentifier("raw_lake", "crm", "contacts_raw");
        var existingTable = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "raw_lake",
                SchemaName = "crm",
                TableName = "contacts_raw",
                Sensitivity = "NORMAL",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "contact_id", DataType = "varchar" },
                new TableColumn { ColumnName = "email", DataType = "varchar" },
                new TableColumn { ColumnName = "ssn_number", DataType = "varchar" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>())
            .Returns(existingTable);

        TableMetadata? upsertedMetadata = null;
        metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => upsertedMetadata = m), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<TableMetadata>()));

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        // Dry-run verification
        using (var dryRunStream = new MemoryStream(Encoding.UTF8.GetBytes(SourcesOnlyManifestJson)))
        {
            var dryRunResult = await service.IngestManifestStreamAsync(dryRunStream, dryRun: true);
            dryRunResult.Success.ShouldBeTrue();
            dryRunResult.ParsedModelsCount.ShouldBe(1);
            dryRunResult.GeneratedProposalsCount.ShouldBeGreaterThanOrEqualTo(2);

            var pendingDryRun = await proposalRepo.GetPendingProposalsAsync(targetTableId);
            pendingDryRun.ShouldBeEmpty();
        }

        // Live ingestion verification
        using (var liveStream = new MemoryStream(Encoding.UTF8.GetBytes(SourcesOnlyManifestJson)))
        {
            var result = await service.IngestManifestStreamAsync(liveStream, dryRun: false);

            result.Success.ShouldBeTrue();
            result.ParsedModelsCount.ShouldBe(1);
            result.GeneratedProposalsCount.ShouldBeGreaterThanOrEqualTo(2);

            // SEC-DBT-01 & SEC-DOC-256-02: proposals created in PendingReview status
            var pendingProposals = await proposalRepo.GetPendingProposalsAsync(targetTableId);
            pendingProposals.Count.ShouldBe(result.GeneratedProposalsCount);
            pendingProposals.ShouldAllBe(p => p.Status == DbtProposalStatus.PendingReview);
            pendingProposals.ShouldAllBe(p => p.Table == targetTableId);

            var emailProposal = pendingProposals.Single(p => p.ColumnName == "email");
            emailProposal.SuggestedRuleType.ShouldBe("MASK_EMAIL");
            emailProposal.SuggestedSensitivity.ShouldBe("HIGH");
            emailProposal.SuggestedOwnerTeam.ShouldBe("DataPlatform");

            var ssnProposal = pendingProposals.Single(p => p.ColumnName == "ssn_number");
            ssnProposal.SuggestedRuleType.ShouldBe("REDACT");
            ssnProposal.SuggestedSensitivity.ShouldBe("HIGH");
            ssnProposal.SuggestedOwnerTeam.ShouldBe("DataPlatform");

            var rlsProposal = pendingProposals.FirstOrDefault(p => p.ColumnName == "*" && p.SuggestedRuleType.StartsWith("RLS_FILTER:"));
            rlsProposal.ShouldNotBeNull();
            rlsProposal.SuggestedRuleType.ShouldBe("RLS_FILTER:tenant_id = @tenant_id");

            // Verify metadata omnichannel documentation sync (F-DOC-01)
            await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
            upsertedMetadata.ShouldNotBeNull();
            upsertedMetadata.Table.Description.ShouldBe("Raw ingested CRM contact records");
            upsertedMetadata.Columns.Single(c => c.ColumnName == "email").Description.ShouldBe("Customer contact email address");
            upsertedMetadata.Columns.Single(c => c.ColumnName == "contact_id").Description.ShouldBe("Primary identifier for contact");
        }
    }
}
