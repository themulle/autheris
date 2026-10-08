namespace Autheris.Extensions.Tests;

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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtTests
{
    private const string SampleDbtManifestJson = """
    {
      "metadata": {
        "dbt_version": "1.8.0",
        "project_name": "corp_analytics"
      },
      "nodes": {
        "model.corp_analytics.stg_customers": {
          "name": "stg_customers",
          "database": "postgres",
          "schema": "raw",
          "description": "Staging table for customers",
          "config": {
            "materialized": "view"
          },
          "contract": {
            "enforced": true
          },
          "tags": ["staging", "finance"],
          "meta": {
            "owner": "FinanceTeam",
            "owner_email": "finance@corp.local"
          },
          "columns": {
            "customer_id": {
              "name": "customer_id",
              "data_type": "integer",
              "description": "Primary key",
              "tags": [],
              "meta": {}
            },
            "email_address": {
              "name": "email_address",
              "data_type": "varchar",
              "description": "Customer contact email",
              "tags": ["pii"],
              "meta": {
                "pii": "true"
              }
            },
            "tax_id": {
              "name": "tax_id",
              "data_type": "varchar",
              "description": "Social security or tax number",
              "tags": ["ssn"],
              "meta": {}
            }
          },
          "depends_on": {
            "nodes": []
          }
        },
        "model.corp_analytics.fct_orders": {
          "name": "fct_orders",
          "database": "postgres",
          "schema": "analytics",
          "description": "Orders fact table",
          "config": {
            "materialized": "table"
          },
          "contract": {
            "enforced": false
          },
          "tags": ["bi"],
          "meta": {
            "owner": "SalesTeam"
          },
          "columns": {
            "order_id": {
              "name": "order_id",
              "data_type": "integer",
              "description": "Order ID",
              "tags": [],
              "meta": {}
            }
          },
          "depends_on": {
            "nodes": [
              "model.corp_analytics.stg_customers"
            ]
          }
        },
        "macro.corp_analytics.test_macro": {
          "name": "test_macro"
        }
      }
    }
    """;

    [Fact]
    public async Task DbtArtifactStreamingParser_ParsesModelsAndFiltersMacros()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);

        models.Count.ShouldBe(2);

        var custModel = models.Single(m => m.Name == "stg_customers");
        custModel.Database.ShouldBe("postgres");
        custModel.Schema.ShouldBe("raw");
        custModel.Materialization.ShouldBe("view");
        custModel.ContractEnforced.ShouldBeTrue();
        custModel.Meta["owner"].ShouldBe("FinanceTeam");
        custModel.Columns.Count.ShouldBe(3);

        var emailCol = custModel.Columns["email_address"];
        emailCol.DataType.ShouldBe("varchar");
        emailCol.Meta["pii"].ShouldBe("true");

        var ordersModel = models.Single(m => m.Name == "fct_orders");
        ordersModel.DependsOnNodes.ShouldContain("model.corp_analytics.stg_customers");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_GeneratesZeroTrustProposals_AndUpdatesLineage()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var addedProposals = new List<DbtMetadataProposal>();
        proposalRepo.AddProposalAsync(Arg.Do<DbtMetadataProposal>(addedProposals.Add), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<DbtMetadataProposal>()));

        IReadOnlyCollection<LineageNode>? capturedNodes = null;
        graphStore.When(g => g.UpdateGraph(Arg.Any<IEnumerable<LineageNode>>()))
            .Do(call => capturedNodes = call.Arg<IEnumerable<LineageNode>>().ToList());

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var result = await service.IngestManifestStreamAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(2);
        result.GeneratedProposalsCount.ShouldBe(2); // email_address and tax_id

        // Check SEC-DBT-01 Zero-Trust: Status must be PendingReview
        addedProposals.Count.ShouldBe(2);
        addedProposals.All(p => p.Status == DbtProposalStatus.PendingReview).ShouldBeTrue();

        var emailProposal = addedProposals.Single(p => p.ColumnName == "email_address");
        emailProposal.SuggestedRuleType.ShouldBe("MASK_EMAIL");
        emailProposal.SuggestedOwnerTeam.ShouldBe("FinanceTeam");
        emailProposal.Table.ShouldBe(new TableIdentifier("postgres", "raw", "stg_customers"));

        var taxProposal = addedProposals.Single(p => p.ColumnName == "tax_id");
        taxProposal.SuggestedRuleType.ShouldBe("REDACT");

        // Verify Lineage Graph
        capturedNodes.ShouldNotBeNull();
        capturedNodes.Count.ShouldBe(2);

        var custNode = capturedNodes.Single(n => n.Name == "stg_customers");
        custNode.DownstreamNodeIds.ShouldContain("postgres.analytics.fct_orders");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_DryRun_DoesNotPersistProposalsOrLineage()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var result = await service.IngestManifestStreamAsync(stream, dryRun: true);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(2);
        result.GeneratedProposalsCount.ShouldBe(2);

        // Verify no repo or graph store updates in dryRun
        await proposalRepo.DidNotReceiveWithAnyArgs().AddProposalAsync(default!, default);
        graphStore.DidNotReceiveWithAnyArgs().UpdateGraph(default!);
    }

    [Fact]
    public async Task DbtExposurePublisher_GeneratesValidYaml()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var logger = NullLogger<DbtExposurePublisher>.Instance;

        var tableList = new List<TableMetadata>
        {
            new()
            {
                Identifier = new TableIdentifier("sales", "dbo", "orders"),
                Table = new Table { SchemaName = "dbo", TableName = "orders" }
            }
        };

        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tableList));

        var publisher = new DbtExposurePublisher(metadataRepo, logger);
        var yaml = await publisher.GenerateExposuresYamlAsync();

        yaml.ShouldContain("version: 2");
        yaml.ShouldContain("exposures:");
        yaml.ShouldContain("autheris_gateway_dbo_orders");
        yaml.ShouldContain("ref('orders')");
        yaml.ShouldContain("https://gateway.corp.local/graphql");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_ApproveProposal_AppliesMaskingRuleAndIncrementsEpoch()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var tableId = new TableIdentifier("postgres", "raw", "stg_customers");
        var proposalId = Guid.NewGuid();
        var proposal = new DbtMetadataProposal(
            Id: proposalId,
            Table: tableId,
            ColumnName: "email_address",
            SuggestedRuleType: "MASK_EMAIL",
            SuggestedSensitivity: "HIGH",
            SuggestedOwnerTeam: "FinanceTeam",
            SourceDbtTag: "pii",
            Status: DbtProposalStatus.PendingReview,
            CreatedAt: DateTimeOffset.UtcNow
        );

        proposalRepo.GetProposalByIdAsync(proposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        proposalRepo.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Approved, "admin", Arg.Any<CancellationToken>())
            .Returns(proposal with { Status = DbtProposalStatus.Approved });

        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "raw", TableName = "stg_customers" },
            Columns = [new TableColumn { ColumnName = "email_address", DataType = "varchar" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingTable);

        TableMetadata? savedMetadata = null;
        metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => savedMetadata = m), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<TableMetadata>()));

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, epochRepo, logger);

        var result = await service.ApproveProposalAsync(proposalId, "admin");

        result.Status.ShouldBe(DbtProposalStatus.Approved);
        savedMetadata.ShouldNotBeNull();
        savedMetadata.ColumnMaskingRules.ContainsKey("email_address").ShouldBeTrue();
        savedMetadata.ColumnMaskingRules["email_address"].RuleType.ShouldBe("MASK_EMAIL");

        await epochRepo.Received(1).IncrementTableEpochAsync(tableId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_ApproveWeakerRule_IsRefused_AndStatusUnchanged()
    {
        // R-EXT-1: an approve blocked by the ratchet must not mark the proposal Approved.
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var tableId = new TableIdentifier("postgres", "raw", "stg_customers");
        var proposalId = Guid.NewGuid();
        proposalRepo.GetProposalByIdAsync(proposalId, Arg.Any<CancellationToken>()).Returns(new DbtMetadataProposal(
            Id: proposalId,
            Table: tableId,
            ColumnName: "email_address",
            SuggestedRuleType: "MASK_EMAIL",
            SuggestedSensitivity: "HIGH",
            SuggestedOwnerTeam: "FinanceTeam",
            SourceDbtTag: "pii",
            Status: DbtProposalStatus.PendingReview,
            CreatedAt: DateTimeOffset.UtcNow));
        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "raw", TableName = "stg_customers" },
            Columns = [new TableColumn { ColumnName = "email_address", DataType = "varchar" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email_address"] = new MaskingRule { RuleType = "REDACT" } }
        });

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, Substitute.For<ILineageGraphStore>(), Substitute.For<IPolicyEpochRepository>(), NullLogger<DbtMetadataIngestionService>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => service.ApproveProposalAsync(proposalId, "admin"));

        await proposalRepo.DidNotReceive().UpdateProposalStatusAsync(Arg.Any<Guid>(), Arg.Any<DbtProposalStatus>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_DeduplicatesPendingProposals()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var tableId = new TableIdentifier("postgres", "raw", "stg_customers");
        var existingPending = new List<DbtMetadataProposal>
        {
            new(
                Id: Guid.NewGuid(),
                Table: tableId,
                ColumnName: "email_address",
                SuggestedRuleType: "MASK_EMAIL",
                SuggestedSensitivity: "HIGH",
                SuggestedOwnerTeam: "FinanceTeam",
                SourceDbtTag: "pii",
                Status: DbtProposalStatus.PendingReview,
                CreatedAt: DateTimeOffset.UtcNow
            )
        };

        proposalRepo.GetPendingProposalsAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingPending);

        var addedProposals = new List<DbtMetadataProposal>();
        proposalRepo.AddProposalAsync(Arg.Do<DbtMetadataProposal>(addedProposals.Add), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<DbtMetadataProposal>()));

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var result = await service.IngestManifestStreamAsync(stream);

        result.Success.ShouldBeTrue();
        // email_address was already pending, so only tax_id is generated
        result.GeneratedProposalsCount.ShouldBe(1);
        addedProposals.Count.ShouldBe(1);
        addedProposals[0].ColumnName.ShouldBe("tax_id");
    }

    [Fact]
    public async Task DbtContractValidator_DetectsBreakingChanges_WhenColumnsDroppedOrTypesChanged()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var logger = NullLogger<DbtContractValidator>.Instance;

        var tableId = new TableIdentifier("postgres", "raw", "stg_customers");
        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "raw", TableName = "stg_customers" },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "email_address", DataType = "varchar" },
                new TableColumn { ColumnName = "tax_id", DataType = "varchar" },
                new TableColumn { ColumnName = "phone_number", DataType = "varchar" } // dropped in manifest!
            ]
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingTable);

        var validator = new DbtContractValidator(metadataRepo, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var result = await validator.ValidateContractsStreamAsync(stream);

        result.IsCompatible.ShouldBeFalse();
        result.ValidatedModelsCount.ShouldBe(1); // only stg_customers has contract.enforced = true
        result.BreakingChanges.Count.ShouldBe(1);
        result.BreakingChanges[0].ChangeType.ShouldBe("DROPPED_COLUMN");
        result.BreakingChanges[0].ColumnName.ShouldBe("phone_number");
    }

    [Fact]
    public async Task DbtContractValidator_Passes_WhenContractIsCompatible()
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var logger = NullLogger<DbtContractValidator>.Instance;

        var tableId = new TableIdentifier("postgres", "raw", "stg_customers");
        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "raw", TableName = "stg_customers" },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "email_address", DataType = "varchar" },
                new TableColumn { ColumnName = "tax_id", DataType = "varchar" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingTable);

        var validator = new DbtContractValidator(metadataRepo, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleDbtManifestJson));
        var result = await validator.ValidateContractsStreamAsync(stream);

        result.IsCompatible.ShouldBeTrue();
        result.BreakingChanges.ShouldBeEmpty();
    }
}

