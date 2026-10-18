namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Data.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class GovernedTableReadPlannerBulkKeysetStreamTests
{
    private static TableMetadata CreateTableMetadata(
        IReadOnlyList<string>? primaryKeys = null,
        IReadOnlyList<string>? requiredFilters = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL",
                RequiredFilterColumns = requiredFilters ?? Array.Empty<string>()
            },
            PrimaryKeyColumns = primaryKeys ?? ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "status", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" },
                new TableColumn { ColumnName = "sensitive_card", DataType = "varchar" }
            ]
        };
    }

    private static TableAccessDecision CreateAllowedDecision(TableIdentifier table, Dictionary<string, ColumnAccessLevel>? columnAccess = null, string? rowFilterSql = null)
    {
        return TableAccessDecision.Allowed(
            table,
            columnAccess ?? new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: rowFilterSql,
            hasUnconstrainedColumnAllow: true);
    }

    [Fact]
    public void PlanKeysetStream_SinglePk_GeneratesExpectedSeekAndOrder()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);
        var encoder = new KeysetCursorEncoder();
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 100 });

        var request = new BulkTableStreamRequest(
            Select: ["id", "status"],
            Cursor: cursor,
            Limit: 50);

        var planned = planner.PlanKeysetStream(request, metadata, decision);

        planned.Sql.ShouldContain("SELECT \"id\", \"status\"");
        planned.Sql.ShouldContain("FROM sales.dbo.orders");
        planned.Sql.ShouldContain("WHERE (\"id\" > @cursor_0)");
        planned.Sql.ShouldContain("ORDER BY \"id\" ASC");
        planned.Sql.ShouldContain("LIMIT 51");
        planned.Parameters["@cursor_0"].ShouldBe(100);
    }

    [Fact]
    public void PlanKeysetStream_CompositePk_GeneratesCompositeMonotonicSeek()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["tenant_id", "id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);
        var encoder = new KeysetCursorEncoder();
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?>
        {
            ["tenant_id"] = "corp",
            ["id"] = 500
        });

        var request = new BulkTableStreamRequest(
            Cursor: cursor,
            Limit: 1000);

        var planned = planner.PlanKeysetStream(request, metadata, decision);

        planned.Sql.ShouldContain("WHERE ((\"tenant_id\" > @cursor_0) OR (\"tenant_id\" = @cursor_0 AND \"id\" > @cursor_1))");
        planned.Sql.ShouldContain("ORDER BY \"tenant_id\" ASC, \"id\" ASC");
        planned.Sql.ShouldContain("LIMIT 1001");
        planned.Parameters["@cursor_0"].ShouldBe("corp");
        planned.Parameters["@cursor_1"].ShouldBe(500);
    }

    [Fact]
    public void PlanKeysetStream_WithFilterAst_BindsParametersAndCombinesWithCursor()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);
        var encoder = new KeysetCursorEncoder();
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 200 });

        using var statusJson = JsonDocument.Parse("\"completed\"");
        using var amountJson = JsonDocument.Parse("50.0");

        var filter = new TypedFilterNode(
            And:
            [
                new TypedFilterNode(Field: "status", Op: "eq", Value: statusJson.RootElement),
                new TypedFilterNode(Field: "amount", Op: "gt", Value: amountJson.RootElement)
            ]);

        var request = new BulkTableStreamRequest(
            Filter: filter,
            Cursor: cursor,
            Limit: 100);

        var planned = planner.PlanKeysetStream(request, metadata, decision);

        planned.Sql.ShouldContain("WHERE (\"status\" = @p0 AND \"amount\" > @p1) AND (\"id\" > @cursor_0)");
        planned.Parameters["@p0"].ShouldBe("completed");
        planned.Parameters["@p1"].ShouldBe(50.0);
        planned.Parameters["@cursor_0"].ShouldBe(200);
    }

    [Fact]
    public void PlanKeysetStream_WithActiveRlsPolicy_ConjoinsAllPredicates()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier, rowFilterSql: "\"tenant_id\" = 'tenant_42'");

        using var statusJson = JsonDocument.Parse("\"active\"");
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "status", Op: "eq", Value: statusJson.RootElement),
            Limit: 50);

        var planned = planner.PlanKeysetStream(request, metadata, decision);

        planned.Sql.ShouldContain("WHERE (\"status\" = @p0) AND (\"tenant_id\" = 'tenant_42')");
    }

    [Fact]
    public void PlanKeysetStream_DottedIdentifier_ThrowsArgumentException()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata();
        var decision = CreateAllowedDecision(metadata.Identifier);

        var filter = new TypedFilterNode(Field: "other_table.column", Op: "eq");
        var request = new BulkTableStreamRequest(Filter: filter);

        var ex = Should.Throw<ArgumentException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("Joins and dotted table identifiers are strictly prohibited");
    }

    [Fact]
    public void PlanKeysetStream_MaskedFilterColumn_ThrowsArgumentException()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["sensitive_card"] = ColumnAccessLevel.Mask
        };
        var decision = CreateAllowedDecision(metadata.Identifier, columnAccess: columnAccess);

        using var cardVal = JsonDocument.Parse("\"4111111111111111\"");
        var filter = new TypedFilterNode(Field: "sensitive_card", Op: "eq", Value: cardVal.RootElement);
        var request = new BulkTableStreamRequest(Filter: filter);

        var ex = Should.Throw<ArgumentException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("Filtering on masked column 'sensitive_card' is rejected");
    }

    [Fact]
    public void PlanKeysetStream_MaskedPrimaryKeyColumn_ThrowsKeysetStreamingUnavailable()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Mask
        };
        var decision = CreateAllowedDecision(metadata.Identifier, columnAccess: columnAccess);

        var request = new BulkTableStreamRequest();
        var ex = Should.Throw<KeysetStreamingUnavailableException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("KEYSET_STREAMING_UNAVAILABLE");
    }

    [Fact]
    public void PlanKeysetStream_MissingRequiredFilterColumn_ThrowsRequiredFilterColumnMissing()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(requiredFilters: ["tenant_id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);

        // Caller filter only constrains status, not tenant_id
        using var statusVal = JsonDocument.Parse("\"active\"");
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "status", Op: "eq", Value: statusVal.RootElement));

        var ex = Should.Throw<RequiredFilterColumnMissingException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("REQUIRED_FILTER_COLUMN_MISSING");
        ex.Message.ShouldContain("tenant_id");
    }
}
