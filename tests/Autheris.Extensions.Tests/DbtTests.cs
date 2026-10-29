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
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtTests
{
    private static readonly TenantId CallerTenant = new("postgres");

    // The direct governance import runs as the calling governance administrator, never as the sync.
    private static readonly DbtGovernanceImportCaller Caller =
        new(new VirtualFilterActor(new Sid("S-1-5-21-LWE-GOVADMIN"), IsSync: false), CallerTenant);

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

    [Fact]
    public async Task DbtArtifactStreamingParser_ParseManifestWithRelationshipsAsync_ExtractsConstraintsAndTestRelationships()
    {
        const string manifestWithRelationshipsJson = """
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
              "columns": {
                "customer_id": { "name": "customer_id", "data_type": "integer" }
              }
            },
            "model.corp_analytics.fct_orders": {
              "name": "fct_orders",
              "database": "postgres",
              "schema": "analytics",
              "columns": {
                "order_id": { "name": "order_id", "data_type": "integer" },
                "customer_id": {
                  "name": "customer_id",
                  "data_type": "integer",
                  "constraints": [
                    {
                      "type": "foreign_key",
                      "to": "ref('stg_customers')",
                      "to_columns": ["customer_id"]
                    }
                  ]
                }
              }
            },
            "test.corp_analytics.relationships_fct_orders_customer_id__customer_id__ref_stg_customers_": {
              "name": "relationships_fct_orders_customer_id__customer_id__ref_stg_customers_",
              "resource_type": "test",
              "test_metadata": {
                "name": "relationships",
                "kwargs": {
                  "column_name": "customer_id",
                  "field": "customer_id",
                  "to": "ref('stg_customers')"
                }
              },
              "attached_node": "model.corp_analytics.fct_orders"
            }
          }
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestWithRelationshipsJson));
        var parsed = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(stream);
        var models = parsed.Models;
        var relationships = parsed.Relationships;

        models.Count.ShouldBe(2);
        relationships.Count.ShouldBeGreaterThanOrEqualTo(1);

        var rel = relationships.First(r => r.ChildModelOrTable == "fct_orders" && r.ParentModelOrTable == "stg_customers");
        rel.ParentColumns.ShouldBe(["customer_id"]);
        rel.ChildColumns.ShouldBe(["customer_id"]);
    }

    [Fact]
    public async Task DbtMetadataIngestionService_SyncMetadataAsync_ImportsDynamicRelationships()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var relationRepo = Substitute.For<ITableRelationRepository>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, relationRepo, logger);

        const string manifestJson = """
        {
          "metadata": {
            "dbt_version": "1.8.0",
            "project_name": "corp_analytics"
          },
          "nodes": {
            "model.corp_analytics.dim_users": {
              "name": "dim_users",
              "database": "postgres",
              "schema": "analytics",
              "columns": {
                "user_id": { "name": "user_id", "data_type": "integer" }
              }
            },
            "model.corp_analytics.fct_orders": {
              "name": "fct_orders",
              "database": "postgres",
              "schema": "analytics",
              "columns": {
                "order_id": { "name": "order_id", "data_type": "integer" },
                "user_id": {
                  "name": "user_id",
                  "data_type": "integer",
                  "constraints": [
                    {
                      "type": "foreign_key",
                      "to": "ref('dim_users')",
                      "field": "user_id"
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
        var result = await service.IngestManifestStreamAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        result.ImportedRelationsCount.ShouldBe(1);
        await relationRepo.Received(1).CreateRelationAsync(Arg.Is<TableRelation>(r =>
            r.RelationName == "dim_users" &&
            r.ParentTableIdentifier.Schema == "analytics" &&
            r.ParentTableIdentifier.TableName == "dim_users" &&
            r.ChildTableIdentifier.Schema == "analytics" &&
            r.ChildTableIdentifier.TableName == "fct_orders"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_IngestGovernanceStreamAsync_ParsesAndUpdatesGovernanceFields()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var targetTableId = new TableIdentifier("postgres", "analytics", "customers");
        var existingMetadata = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "postgres",
                SchemaName = "analytics",
                TableName = "customers",
                Sensitivity = "NORMAL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "email_address", DataType = "varchar" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>())
            .Returns(existingMetadata);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([existingMetadata]);

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        const string governanceJson = """
        {
          "classifications": [
            {
              "database": "postgres",
              "schema": "analytics",
              "table": "customers",
              "sensitivity": "CONFIDENTIAL",
              "description": "Customer entity table",
              "origin": "dbt_governance_v1",
              "classification_review": {
                "reviewer": "compliance_lead",
                "status": "APPROVED"
              },
              "columns": {
                "email_address": {
                  "sensitivity": "HIGH",
                  "description": "PII Email address",
                  "masking_rule": "MASK_EMAIL"
                }
              }
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(governanceJson));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeTrue();
        result.UpdatedTablesCount.ShouldBe(1);
        result.UpdatedColumnsCount.ShouldBe(1);
        result.MaskingRulesCount.ShouldBe(1);

        await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m =>
            m.Table.Sensitivity == "CONFIDENTIAL" &&
            m.Table.DocumentationSource == "dbt_governance_v1" &&
            m.ColumnMaskingRules.ContainsKey("email_address") &&
            m.Columns.First(c => c.ColumnName == "email_address").IsSensitive
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_B01_IgnoresNoneNullFalseMaskingRule_AndRejectsUnknownRule()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var targetTableId = new TableIdentifier("postgres", "analytics", "customers");
        var existingMetadata = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "postgres",
                SchemaName = "analytics",
                TableName = "customers",
                Sensitivity = "NORMAL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "col_none", DataType = "varchar" },
                new TableColumn { ColumnName = "col_null", DataType = "varchar" },
                new TableColumn { ColumnName = "col_false", DataType = "varchar" }
            ]
        };

        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>())
            .Returns(existingMetadata);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([existingMetadata]);

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        const string validJson = """
        {
          "classifications": [
            {
              "database": "postgres",
              "schema": "analytics",
              "table": "customers",
              "columns": {
                "col_none": { "masking_rule": "none" },
                "col_null": { "masking_rule": "null" },
                "col_false": { "masking_rule": "false" }
              }
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(validJson));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeTrue();
        result.MaskingRulesCount.ShouldBe(0);

        const string unknownRuleJson = """
        {
          "classifications": [
            {
              "database": "postgres",
              "schema": "analytics",
              "table": "customers",
              "columns": {
                "col_none": { "masking_rule": "SUPER_SECRET_ALGO" }
              }
            }
          ]
        }
        """;

        using var badStream = new MemoryStream(Encoding.UTF8.GetBytes(unknownRuleJson));
        var ex = await Should.ThrowAsync<ArgumentException>(() => service.IngestGovernanceStreamAsync(badStream, Caller, dryRun: false));
        ex.Message.ShouldContain("SUPER_SECRET_ALGO");
    }

    [Theory]
    [InlineData("email_mask", "MASK_EMAIL")]
    [InlineData("EMAIL_MASK", "MASK_EMAIL")]
    [InlineData(" Phone_Mask ", "MASK_PHONE")]
    [InlineData("iban_mask", "MASK_IBAN")]
    [InlineData("mask_email", "MASK_EMAIL")]
    [InlineData("mask_partial", "PARTIAL_MASK")]
    [InlineData("partial_mask", "PARTIAL_MASK")]
    public async Task DbtMetadataIngestionService_I393a_NormalisesMaskAliasesToExistingRules(string alias, string expected)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var targetTableId = new TableIdentifier("postgres", "analytics", "customers");
        var existingMetadata = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "postgres", SchemaName = "analytics", TableName = "customers", Sensitivity = "NORMAL" },
            Columns = [new TableColumn { ColumnName = "col_a", DataType = "varchar" }]
        };
        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>()).Returns(existingMetadata);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([existingMetadata]);
        var service = new DbtMetadataIngestionService(Substitute.For<IDbtProposalRepository>(), metadataRepo, Substitute.For<ILineageGraphStore>(), NullLogger<DbtMetadataIngestionService>.Instance);

        var json = "{ \"classifications\": [ { \"database\": \"postgres\", \"schema\": \"analytics\", \"table\": \"customers\", \"columns\": { \"col_a\": { \"masking_rule\": \"" + alias + "\" } } } ] }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeTrue();
        await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.ColumnMaskingRules["col_a"].RuleType == expected), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("geo_mask")]
    [InlineData("mask_geo_jitter")]
    [InlineData("emal_mask")]
    public async Task DbtMetadataIngestionService_I393a_UnknownMaskAliasesStillFailClosed(string alias)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var targetTableId = new TableIdentifier("postgres", "analytics", "customers");
        var existingMetadata = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "postgres", SchemaName = "analytics", TableName = "customers", Sensitivity = "NORMAL" },
            Columns = [new TableColumn { ColumnName = "col_a", DataType = "varchar" }]
        };
        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>()).Returns(existingMetadata);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([existingMetadata]);
        var service = new DbtMetadataIngestionService(Substitute.For<IDbtProposalRepository>(), metadataRepo, Substitute.For<ILineageGraphStore>(), NullLogger<DbtMetadataIngestionService>.Instance);
        var json = "{ \"classifications\": [ { \"database\": \"postgres\", \"schema\": \"analytics\", \"table\": \"customers\", \"columns\": { \"col_a\": { \"masking_rule\": \"" + alias + "\" } } } ] }";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var ex = await Should.ThrowAsync<ArgumentException>(() => service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false));
        ex.Message.ShouldContain("Unknown masking rule");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_B02_ReplaceMode_RejectsRemovalOfOmittedRules_AndWritesNothing()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var targetTableId = new TableIdentifier("postgres", "analytics", "customers");
        var existingMetadata = new TableMetadata
        {
            Identifier = targetTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "postgres",
                SchemaName = "analytics",
                TableName = "customers"
            },
            Columns =
            [
                new TableColumn { ColumnName = "col_a", DataType = "varchar" },
                new TableColumn { ColumnName = "col_b", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["col_a"] = new MaskingRule { RuleType = "REDACT" },
                ["col_b"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        metadataRepo.GetTableMetadataAsync(targetTableId, Arg.Any<CancellationToken>())
            .Returns(existingMetadata);
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([existingMetadata]);

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        // Only col_a is supplied with a rule; col_b is omitted
        const string replaceJson = """
        {
          "classifications": [
            {
              "database": "postgres",
              "schema": "analytics",
              "table": "customers",
              "columns": {
                "col_a": { "masking_rule": "REDACT" }
              }
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(replaceJson));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false, replace: true);

        // Fail closed: removing col_b's rule is a relaxation, so the whole import is rejected and nothing is written.
        result.Success.ShouldBeFalse();
        result.RemovedMaskingRulesCount.ShouldBe(1);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("col_b");
        result.ErrorMessage.ShouldContain("proposal workflow");

        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    private static TableMetadata GovernanceTable(string tableName, string sensitivity, IReadOnlyDictionary<string, MaskingRule>? rules = null, bool sensitiveColumn = false) => new()
    {
        Identifier = new TableIdentifier("postgres", "analytics", tableName),
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "postgres",
            SchemaName = "analytics",
            TableName = tableName,
            Sensitivity = sensitivity
        },
        Columns =
        [
            new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = sensitiveColumn }
        ],
        ColumnMaskingRules = rules ?? new Dictionary<string, MaskingRule>()
    };

    private static (DbtMetadataIngestionService Service, ITableMetadataRepository Repo) GovernanceService(params TableMetadata[] tables)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        foreach (var table in tables)
        {
            metadataRepo.GetTableMetadataAsync(table.Identifier, Arg.Any<CancellationToken>()).Returns(table);
        }

        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables.ToList());
        var service = new DbtMetadataIngestionService(
            Substitute.For<IDbtProposalRepository>(), metadataRepo, Substitute.For<ILineageGraphStore>(), NullLogger<DbtMetadataIngestionService>.Instance);
        return (service, metadataRepo);
    }

    [Theory]
    [InlineData("\"masking_rule\": \"MASK_EMAIL\"")]
    [InlineData("\"masking_rule\": null")]
    [InlineData("\"masking_rule\": \"none\"")]
    public async Task DbtMetadataIngestionService_GovernanceImport_RejectsAWeakenedOrRemovedMaskingRule_AndWritesNoTable(string ruleJson)
    {
        var clean = GovernanceTable("clean", "NORMAL");
        var nullified = GovernanceTable("customers", "NORMAL", new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "NULLIFY" } });
        var (service, metadataRepo) = GovernanceService(clean, nullified);

        var json = $$"""
        {
          "classifications": [
            { "database": "postgres", "schema": "analytics", "table": "clean", "columns": { "email": { "masking_rule": "REDACT" } } },
            { "database": "postgres", "schema": "analytics", "table": "customers", "columns": { "email": { {{ruleJson}} } } }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        // Same ratchet as ApproveProposalAsync; the valid change on "clean" is not written either (all or nothing).
        result.Success.ShouldBeFalse();
        (result.RelaxedMaskingRulesCount + result.RemovedMaskingRulesCount).ShouldBe(1);
        result.ErrorMessage!.ShouldContain("customers.email");
        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_GovernanceImport_AppliesATightenedMaskingRule()
    {
        var table = GovernanceTable("customers", "NORMAL", new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" } });
        var (service, metadataRepo) = GovernanceService(table);

        const string json = """
        { "classifications": [ { "database": "postgres", "schema": "analytics", "table": "customers", "columns": { "email": { "masking_rule": "NULLIFY" } } } ] }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false, replace: true);

        result.Success.ShouldBeTrue();
        result.MaskingRulesCount.ShouldBe(1);
        await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m =>
            m.ColumnMaskingRules["email"].RuleType == "NULLIFY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DbtMetadataIngestionService_GovernanceImport_RejectsLoweredTableAndColumnSensitivity()
    {
        var table = GovernanceTable("customers", "CONFIDENTIAL", sensitiveColumn: true);
        var (service, metadataRepo) = GovernanceService(table);

        const string json = """
        { "classifications": [ { "database": "postgres", "schema": "analytics", "table": "customers", "sensitivity": "PUBLIC",
                                 "columns": { "email": { "sensitivity": "LOW" } } } ] }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Contains("Table 'postgres.analytics.customers'") && w.Contains("sensitivity 'PUBLIC'"));
        result.Warnings.ShouldContain(w => w.Contains("customers.email") && w.Contains("sensitivity 'LOW'"));
        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    // An empty stored table sensitivity is the implicit NORMAL: lowering it to PUBLIC/LOW is rejected like lowering an explicit NORMAL.
    [Theory]
    [InlineData("", "PUBLIC")]
    [InlineData("   ", "LOW")]
    [InlineData("NORMAL", "PUBLIC")]
    public async Task DbtMetadataIngestionService_GovernanceImport_RejectsLoweringAnImplicitNormalTableSensitivity(string stored, string incoming)
    {
        var table = GovernanceTable("customers", stored);
        var (service, metadataRepo) = GovernanceService(table);

        var json = $$"""
        { "classifications": [ { "database": "postgres", "schema": "analytics", "table": "customers", "sensitivity": "{{incoming}}" } ] }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Contains("Table 'postgres.analytics.customers'") && w.Contains($"sensitivity '{incoming}'") && w.Contains("'NORMAL'"));
        await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("\"required_filter_columns\": [\"tenant_id\"]", false, false)]
    [InlineData("\"required_filter_columns\": []", false, false)]
    [InlineData("\"required_filter_column_groups\": [[\"client_id\"]]", false, false)]
    [InlineData("\"required_filter_column_groups\": [[\"client_id\", \"year\"], [\"region\"]]", false, false)]
    [InlineData("\"description\": \"no requirements given\"", true, false)]
    [InlineData("\"description\": \"no requirements given\"", false, true)]
    [InlineData("\"required_filter_columns\": [\"client_id\", \"tenant_id\", \"year\"], \"required_filter_column_groups\": [[\"client_id\", \"year\"], [\"client_id\", \"year\", \"month\"]]", true, true)]
    public async Task DbtMetadataIngestionService_GovernanceImport_RequiredFilterColumnsAndGroups_MayOnlyBeTightened(string requirementJson, bool replace, bool expectedSuccess)
    {
        var baseTable = GovernanceTable("customers", "NORMAL");
        var table = baseTable with
        {
            Table = baseTable.Table with
            {
                RequiredFilterColumns = ["client_id", "tenant_id"],
                RequiredFilterColumnGroups = [["client_id", "year"]]
            }
        };
        var (service, metadataRepo) = GovernanceService(table);
        var json = $$"""
        { "classifications": [ { "database": "postgres", "schema": "analytics", "table": "customers", {{requirementJson}} } ] }
        """;

        using var dryStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var dry = await service.IngestGovernanceStreamAsync(dryStream, Caller, dryRun: true, replace: replace);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false, replace: replace);

        // Fail closed: removing a required column / group (also by omission in replace mode) or adding a weaker alternative group
        // rejects the import, identically in a dry run; adding requirements is applied.
        dry.Success.ShouldBe(expectedSuccess);
        result.Success.ShouldBe(expectedSuccess);
        if (expectedSuccess)
        {
            await metadataRepo.Received(1).UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        }
        else
        {
            result.ErrorMessage!.ShouldContain("required filter column");
            await metadataRepo.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task DbtMetadataIngestionService_GovernanceImport_RejectsTheSyncActor()
    {
        var (service, _) = GovernanceService(GovernanceTable("customers", "NORMAL"));
        var syncCaller = new DbtGovernanceImportCaller(new VirtualFilterActor(new Sid("S-1-5-21-DBT-SYNC"), IsSync: true), CallerTenant);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        await Should.ThrowAsync<ArgumentException>(() => service.IngestGovernanceStreamAsync(stream, syncCaller));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DbtMetadataIngestionService_GovernanceImport_AcceptsAForeignPayloadTenantOnlyForClusterAdmin(bool isClusterAdmin, bool expectedSuccess)
    {
        var (service, _) = GovernanceService();
        var caller = Caller with { IsClusterAdmin = isClusterAdmin };

        const string json = """
            {"access_profiles":{"david":{"tenant":"tenant_other","scope":"lwetem_prod.*.*","uncovered":"deny",
              "grantee":{"type":"user","sid":"S-1-5-21-LWE-DAVID"},
              "bindings":[{"filter":"nicht_ausgelieferte_krane","target":"lwetem_prod.*.*.client_id"}]}}}
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await service.IngestGovernanceStreamAsync(stream, caller, dryRun: true);

        result.Success.ShouldBe(expectedSuccess);
        if (!expectedSuccess)
        {
            result.ErrorMessage!.ShouldContain("tenant_other");
        }
    }

    [Fact]
    public void DbtArtifactStreamingParser_B04_ExtractModelName_ResolvesSourceExpressions()
    {
        var result1 = DbtArtifactStreamingParser.ExtractModelName("source('lwetem_prod_conf', 'client')");
        result1.ShouldBe("lwetem_prod_conf.client");

        var result2 = DbtArtifactStreamingParser.ExtractModelName("source(\"crm\", \"accounts\")");
        result2.ShouldBe("crm.accounts");

        var result3 = DbtArtifactStreamingParser.ExtractModelName("ref('stg_orders')");
        result3.ShouldBe("stg_orders");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_R51_RoutinesProduceRoutineWarnings()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([]);

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, logger);

        const string routineJson = """
        {
          "classifications": [
            {
              "database": "postgres",
              "schema": "dbo",
              "table": "sp_calculate_tax"
            },
            {
              "database": "postgres",
              "schema": "dbo",
              "table": "fn_haversine"
            }
          ]
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(routineJson));
        var result = await service.IngestGovernanceStreamAsync(stream, Caller, dryRun: false);

        result.Success.ShouldBeTrue();
        result.Warnings.Count.ShouldBe(2);
        result.Warnings.ShouldAllBe(w => w.Contains("nicht anwendbar (Routine)"));
    }
}


