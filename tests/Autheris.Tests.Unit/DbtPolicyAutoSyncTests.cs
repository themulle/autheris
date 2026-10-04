namespace Autheris.Tests.Unit;

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

public sealed class DbtPolicyAutoSyncTests
{
    private const string PolicyManifestJson = """
    {
      "nodes": {
        "model.analytics.finance_orders": {
          "name": "finance_orders",
          "database": "corp_dw",
          "schema": "finance",
          "description": "Financial orders model with automated policy constraints",
          "config": { "materialized": "table" },
          "tags": ["finance"],
          "meta": {
            "owner": "FinanceDataTeam",
            "casbin_roles": "FinanceAdmin,Auditor",
            "rls_filter": "tenant_id = @current_tenant"
          },
          "columns": {
            "order_id": {
              "name": "order_id",
              "data_type": "integer",
              "description": "Primary order ID",
              "tags": [],
              "meta": {}
            },
            "amount": {
              "name": "amount",
              "data_type": "decimal",
              "description": "Order net amount",
              "tags": [],
              "meta": {
                "rls_filter": "amount > 0"
              }
            }
          },
          "depends_on": { "nodes": [] }
        }
      }
    }
    """;

    [Fact]
    public async Task IngestManifestStreamAsync_GeneratesPolicyAndRlsProposals()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var proposalRepo = new InMemoryDbtProposalRepository();
        var lineageStore = Substitute.For<ILineageGraphStore>();

        var tableId = new TableIdentifier("corp_dw", "finance", "finance_orders");
        var existingTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { SchemaName = "finance", TableName = "finance_orders" },
            PrimaryKeyColumns = ["order_id"],
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "integer" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" }
            ]
        };

        metaRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(existingTable));

        var ingestionService = new DbtMetadataIngestionService(
            proposalRepo,
            metaRepo,
            lineageStore,
            NullLogger<DbtMetadataIngestionService>.Instance
        );

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PolicyManifestJson));
        var syncResult = await ingestionService.IngestManifestStreamAsync(stream, dryRun: false);

        syncResult.Success.ShouldBeTrue();
        syncResult.ParsedModelsCount.ShouldBe(1);
        syncResult.GeneratedProposalsCount.ShouldBe(3); // 1 casbin_roles + 1 model rls_filter + 1 column rls_filter

        var pending = await proposalRepo.GetPendingProposalsAsync(tableId);
        pending.Count.ShouldBe(3);

        var casbinProposal = pending.FirstOrDefault(p => p.SourceDbtTag == "meta.casbin_roles");
        casbinProposal.ShouldNotBeNull();
        casbinProposal.SuggestedRuleType.ShouldBe("CASBIN_ROLES:FinanceAdmin,Auditor");
        casbinProposal.SuggestedOwnerTeam.ShouldBe("FinanceDataTeam");

        var modelRlsProposal = pending.FirstOrDefault(p => p.SourceDbtTag == "meta.rls_filter" && p.ColumnName == "*");
        modelRlsProposal.ShouldNotBeNull();
        modelRlsProposal.SuggestedRuleType.ShouldBe("RLS_FILTER:tenant_id = @current_tenant");

        var colRlsProposal = pending.FirstOrDefault(p => p.SourceDbtTag == "meta.rls_filter" && p.ColumnName == "amount");
        colRlsProposal.ShouldNotBeNull();
        colRlsProposal.SuggestedRuleType.ShouldBe("RLS_FILTER:amount > 0");

        // Test Approval of policy proposal
        var approved = await ingestionService.ApproveProposalAsync(casbinProposal.Id, "security_officer");
        approved.Status.ShouldBe(DbtProposalStatus.Approved);
        approved.ReviewedBy.ShouldBe("security_officer");
    }
}
