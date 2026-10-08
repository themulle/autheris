namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-2 / R-EXT-2: Batch und Webhook behalten SourceName; dbt-Approve behält alle Eigenschaften.
/// </summary>
public sealed class DbtSourceNamePreservationTests
{
    [Fact]
    public void CatalogGovernanceRatchet_PreservesSourceNameAndConnectionProperties()
    {
        var tableId = new TableIdentifier("sales", "crm", "customers");

        var existing = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "dedicated_crm_db",
                SchemaName = "crm",
                TableName = "customers",
                DataSourceType = DataSourceType.HttpPlugin,
                PluginName = "SalesforcePlugin",
                HttpEndpoint = new HttpEndpointDescriptor { BaseUrl = "https://salesforce.local/api" },
                Sensitivity = "HIGH",
                RequiresFourEyes = true,
                DisplayName = "CRM Customers",
                Description = "Existing core description",
                LongDescription = "Detailed documentation",
                DocumentationSource = "AdminOverride",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "int", IsSensitive = false },
                new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        // Incoming metadata from an external catalog trying to reset SourceName or weaken security
        var incoming = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.Empty,
                SourceName = "external_catalog_name", // Must be ignored in favor of existing
                SchemaName = "crm",
                TableName = "customers",
                DataSourceType = DataSourceType.Sql, // Must NOT override existing plugin
                PluginName = null,
                HttpEndpoint = null,
                Sensitivity = "LOW", // Ratchet: cannot weaken HIGH
                RequiresFourEyes = false, // Ratchet: cannot weaken true
                DisplayName = "Updated Display Name",
                Description = "Sync updated description",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = new MaskingRule { RuleType = "HMAC" } // Weaker than REDACT: must be rejected
            }
        };

        var merged = CatalogGovernanceRatchet.Merge(incoming, existing);

        // Assert: Connection properties strictly preserved
        merged.Table.SourceName.ShouldBe("dedicated_crm_db");
        merged.Table.DataSourceType.ShouldBe(DataSourceType.HttpPlugin);
        merged.Table.PluginName.ShouldBe("SalesforcePlugin");
        merged.Table.HttpEndpoint?.BaseUrl.ShouldBe("https://salesforce.local/api");

        // Assert: Governance settings cannot be weakened
        merged.Table.Sensitivity.ShouldBe("HIGH");
        merged.Table.RequiresFourEyes.ShouldBeTrue();

        // Assert: Masking rule strength preserved (REDACT is stronger than HMAC)
        merged.ColumnMaskingRules["ssn"].RuleType.ShouldBe("REDACT");

        // Assert: Columns merged without dropping existing
        merged.Columns.ShouldContain(c => c.ColumnName == "customer_id");
        merged.Columns.ShouldContain(c => c.ColumnName == "ssn");
        merged.Columns.ShouldContain(c => c.ColumnName == "email");
    }

    [Fact]
    public async Task DbtMetadataIngestionService_ApproveProposal_PreservesAllTableAndConnectionProperties()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();
        var logger = NullLogger<DbtMetadataIngestionService>.Instance;

        var tableId = new TableIdentifier("sales", "crm", "stg_customers");
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
            CreatedAt: DateTimeOffset.UtcNow);

        proposalRepo.GetProposalByIdAsync(proposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        proposalRepo.UpdateProposalStatusAsync(proposalId, DbtProposalStatus.Approved, "compliance-admin", Arg.Any<CancellationToken>())
            .Returns(proposal with { Status = DbtProposalStatus.Approved });

        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "custom_crm_connection",
                SchemaName = "crm",
                TableName = "stg_customers",
                DataSourceType = DataSourceType.HttpPlugin,
                PluginName = "CustomCrmPlugin",
                HttpEndpoint = new HttpEndpointDescriptor { BaseUrl = "https://crm.local/data" },
                Sensitivity = "RESTRICTED",
                RequiresFourEyes = true,
                DisplayName = "Staging Customers",
                Description = "Internal customer staging",
                LongDescription = "Full staging table documentation",
                DocumentationSource = "DBT",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "email_address", DataType = "varchar" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        };

        metadataRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(existingTable);

        TableMetadata? savedMetadata = null;
        metadataRepo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(m => savedMetadata = m), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<TableMetadata>()));

        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, epochRepo, logger);

        var result = await service.ApproveProposalAsync(proposalId, "compliance-admin");

        result.Status.ShouldBe(DbtProposalStatus.Approved);
        savedMetadata.ShouldNotBeNull();

        // Verify that ALL table connection and governance attributes are 100% preserved
        savedMetadata.Table.SourceName.ShouldBe("custom_crm_connection");
        savedMetadata.Table.DataSourceType.ShouldBe(DataSourceType.HttpPlugin);
        savedMetadata.Table.PluginName.ShouldBe("CustomCrmPlugin");
        savedMetadata.Table.HttpEndpoint?.BaseUrl.ShouldBe("https://crm.local/data");
        savedMetadata.Table.Sensitivity.ShouldBe("RESTRICTED");
        savedMetadata.Table.RequiresFourEyes.ShouldBeTrue();
        savedMetadata.Table.DisplayName.ShouldBe("Staging Customers");
        savedMetadata.Table.Description.ShouldBe("Internal customer staging");
        savedMetadata.Table.LongDescription.ShouldBe("Full staging table documentation");
        savedMetadata.Table.DocumentationSource.ShouldBe("DBT");
        savedMetadata.Table.IsActive.ShouldBeTrue();

        // Verify the proposal rule was applied
        savedMetadata.ColumnMaskingRules.ContainsKey("email_address").ShouldBeTrue();
        savedMetadata.ColumnMaskingRules["email_address"].RuleType.ShouldBe("MASK_EMAIL");

        // Verify epoch was incremented
        await epochRepo.Received(1).IncrementTableEpochAsync(tableId, Arg.Any<CancellationToken>());
    }
}
