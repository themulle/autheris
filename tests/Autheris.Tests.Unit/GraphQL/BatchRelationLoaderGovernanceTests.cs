namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class BatchRelationLoaderGovernanceTests
{
    private static (BatchRelationLoader Loader, IDataSourceExecutor Executor, ITableMetadataRepository Repo) CreateFixture(
        TableAccessDecision decision,
        TableMetadata? customMetadata = null)
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var metadata = customMetadata ?? new TableMetadata
        {
            Identifier = childTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "finance",
                SchemaName = "dbo",
                TableName = "finance_items",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "parent_id", DataType = "varchar" },
                new TableColumn { ColumnName = "product_name", DataType = "varchar" },
                new TableColumn { ColumnName = "price", DataType = "decimal" },
                new TableColumn { ColumnName = "sensitive_note", DataType = "varchar" }
            ]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(childTableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var executor = Substitute.For<IDataSourceExecutor>();
        executor.SupportedType.Returns(DataSourceType.Sql);

        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        maskingProvider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>())
            .Returns(ci => "[MASKED]");

        var chunkedExecutor = new ChunkedQueryExecutor();
        var options = new GatewayOptions();

        var loader = new BatchRelationLoader(
            repo,
            [executor],
            maskingProvider,
            chunkedExecutor,
            drainController: null,
            options,
            checkTableAccess: (p, t, ct) => Task.FromResult(decision));

        return (loader, executor, repo);
    }

    private static ClaimsPrincipal CreatePrincipal(string tenant = "tenant-a")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new("tenant_id", tenant)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public async Task LoadItems_DeniedIdColumn_DoesNotEmitRawId()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        // Deny access to "id" column
        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Deny,
            ["parent_id"] = ColumnAccessLevel.Clear,
            ["product_name"] = ColumnAccessLevel.Clear
        };
        var decision = new TableAccessDecision(childTableId, true, colAccess, null, []);

        var (loader, executor, _) = CreateFixture(decision);

        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = "raw-item-12345",
                ["parent_id"] = "inv-1",
                ["product_name"] = "Widget"
            }
        };
        executor.ExecuteAsync(Arg.Any<DataSourceExecutionContext>(), Arg.Any<CancellationToken>()).Returns(rawRows);

        var result = await loader.LoadInvoiceItemsBatchAsync(CreatePrincipal(), ["inv-1"]);

        result.ShouldContainKey("inv-1");
        result["inv-1"].Count.ShouldBe(1);
        // The id must NOT be the raw id; it must be null because column "id" was denied
        result["inv-1"][0].Id.ShouldBeNull();
        result["inv-1"][0].ProductName.ShouldBe("Widget");
    }

    [Fact]
    public async Task LoadItems_MaskedParentId_RelationNotResolved_ReportsRelationKeyMasked()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        // Mask access to parent_id column
        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["parent_id"] = ColumnAccessLevel.Mask,
            ["product_name"] = ColumnAccessLevel.Clear
        };
        var decision = new TableAccessDecision(childTableId, true, colAccess, null, []);

        var (loader, executor, _) = CreateFixture(decision);

        var result = await loader.LoadInvoiceItemsBatchAsync(CreatePrincipal(), ["inv-1"]);

        // Whole relation refused before execution
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        result["inv-1"].ShouldBeEmpty();
        loader.LastRefusalReason.ShouldBe(CrossSourceRelationErrorCodes.RelationKeyMasked);
        loader.Reasons.ShouldContain(CrossSourceRelationErrorCodes.RelationKeyMasked);
    }

    [Fact]
    public async Task LoadItems_RlsDropsRows_ItemsNotAttachedToWrongInvoice()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        // Row filter: tenant_id = 'tenant-a'
        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["parent_id"] = ColumnAccessLevel.Clear,
            ["product_name"] = ColumnAccessLevel.Clear,
            ["tenant_id"] = ColumnAccessLevel.Clear
        };
        // Row filter drops rows where tenant_id != tenant-a
        var decision = new TableAccessDecision(
            childTableId,
            true,
            colAccess,
            CombinedRowFilterSql: "tenant_id = 'tenant-a'",
            DeniedReasons: []);

        var meta = new TableMetadata
        {
            Identifier = childTableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "finance",
                SchemaName = "dbo",
                TableName = "finance_items",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "varchar" },
                new TableColumn { ColumnName = "parent_id", DataType = "varchar" },
                new TableColumn { ColumnName = "product_name", DataType = "varchar" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var (loader, executor, _) = CreateFixture(decision, meta);

        // Raw rows:
        // Row 0 has tenant_id = 'other-tenant' (inv-1)
        // Row 1 has tenant_id = 'tenant-a' (inv-2)
        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = "item-foreign",
                ["parent_id"] = "inv-1",
                ["product_name"] = "Secret Widget",
                ["tenant_id"] = "other-tenant"
            },
            new Dictionary<string, object?>
            {
                ["id"] = "item-own",
                ["parent_id"] = "inv-2",
                ["product_name"] = "My Widget",
                ["tenant_id"] = "tenant-a"
            }
        };
        executor.ExecuteAsync(Arg.Any<DataSourceExecutionContext>(), Arg.Any<CancellationToken>()).Returns(rawRows);

        var result = await loader.LoadInvoiceItemsBatchAsync(CreatePrincipal("tenant-a"), ["inv-1", "inv-2"]);

        // inv-1 must have 0 items (its raw row was dropped by RLS filter)
        result["inv-1"].ShouldBeEmpty();
        // inv-2 must have item-own (and NOT item-foreign)
        result["inv-2"].Count.ShouldBe(1);
        result["inv-2"][0].Id.ShouldBe("item-own");
        result["inv-2"][0].InvoiceId.ShouldBe("inv-2");
    }

    [Fact]
    public async Task LoadItems_InvoiceIdAliasOnlyInRaw_IsNotUsed()
    {
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var colAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["parent_id"] = ColumnAccessLevel.Clear,
            ["product_name"] = ColumnAccessLevel.Clear
        };
        var decision = new TableAccessDecision(childTableId, true, colAccess, null, []);

        var (loader, executor, _) = CreateFixture(decision);

        // In raw, there is invoice_id alias, but parent_id is missing/null in the schema and governed row
        var rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = "item-1",
                ["parent_id"] = null,
                ["invoice_id"] = "inv-1",
                ["product_name"] = "Widget"
            }
        };
        executor.ExecuteAsync(Arg.Any<DataSourceExecutionContext>(), Arg.Any<CancellationToken>()).Returns(rawRows);

        var result = await loader.LoadInvoiceItemsBatchAsync(CreatePrincipal(), ["inv-1"]);

        // Since parent_id was null in the governed output, raw invoice_id must NOT be used as a fallback
        result["inv-1"].ShouldBeEmpty();
        loader.Reasons.ShouldContain(CrossSourceRelationErrorCodes.RelationKeyMasked);
    }
}
