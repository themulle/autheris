namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Application.Data.Interfaces;
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
                RequiredFilterColumns = [.. (requiredFilters ?? [])]
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

    private const string Tenant = "tenant-1";
    private const string Caller = "S-1-5-21-caller";

    private static string IssueCursor(
        IReadOnlyDictionary<string, object?> pkValues,
        TableMetadata metadata,
        BulkTableStreamRequest request,
        IKeysetCursorEncoder? encoder = null,
        string tenant = Tenant,
        string caller = Caller)
    {
        // Exactly what TableStreamEndpoints does when it issues nextCursor.
        var context = KeysetCursorContextFactory.ForBulkStream(tenant, metadata, metadata.PrimaryKeyColumns, caller, request);
        return (encoder ?? new KeysetCursorEncoder()).EncodeCursor(pkValues, context);
    }

    [Fact]
    public void PlanKeysetStream_CursorIssuedForSameContext_DecodesWithInjectedEncoder()
    {
        var encoder = new KeysetCursorEncoder("bulk-stream-signing-key-for-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);
        var request = new BulkTableStreamRequest(Select: ["id", "status"], Limit: 10);
        var cursor = IssueCursor(new Dictionary<string, object?> { ["id"] = 7 }, metadata, request, encoder);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor, Limit = 20 }, metadata, decision, Tenant, Caller);

        planned.Parameters["@cursor_0"].ShouldBe(7);
    }

    [Fact]
    public void PlanKeysetStream_CursorReplayedAcrossTenantCallerTableOrRequest_FailsClosed()
    {
        var encoder = new KeysetCursorEncoder("bulk-stream-signing-key-for-tests-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier);
        using var statusJson = JsonDocument.Parse("\"open\"");
        using var otherStatusJson = JsonDocument.Parse("\"closed\"");
        var request = new BulkTableStreamRequest(
            Select: ["id", "status"],
            Filter: new TypedFilterNode(Field: "status", Op: "eq", Value: statusJson.RootElement),
            Limit: 10);
        var cursor = IssueCursor(new Dictionary<string, object?> { ["id"] = 7 }, metadata, request, encoder);
        var continuation = request with { Cursor = cursor };

        // Other tenant
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation, metadata, decision, "tenant-2", Caller));

        // Other caller
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation, metadata, decision, Tenant, "S-1-5-21-other"));

        // Missing tenant / caller binding
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation, metadata, decision));

        // Other table with the same key shape
        var otherTable = CreateTableMetadata(primaryKeys: ["id"]) with { Identifier = new TableIdentifier("sales", "dbo", "invoices") };
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation, otherTable, CreateAllowedDecision(otherTable.Identifier), Tenant, Caller));

        // Other schema
        var otherSchema = CreateTableMetadata(primaryKeys: ["id"]) with { Identifier = new TableIdentifier("sales", "archive", "orders") };
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation, otherSchema, CreateAllowedDecision(otherSchema.Identifier), Tenant, Caller));

        // Other filter
        var otherFilter = continuation with { Filter = new TypedFilterNode(Field: "status", Op: "eq", Value: otherStatusJson.RootElement) };
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(otherFilter, metadata, decision, Tenant, Caller));

        // Dropped filter
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation with { Filter = null }, metadata, decision, Tenant, Caller));

        // Other projection
        Should.Throw<InvalidCursorException>(() => planner.PlanKeysetStream(continuation with { Select = ["id", "amount"] }, metadata, decision, Tenant, Caller));

        // Other signing key
        var otherKeyPlanner = new GovernedTableReadPlanner(new KeysetCursorEncoder("a-different-bulk-stream-signing-key-987654321"));
        Should.Throw<InvalidCursorException>(() => otherKeyPlanner.PlanKeysetStream(continuation, metadata, decision, Tenant, Caller));

        // Identical context still decodes
        planner.PlanKeysetStream(continuation, metadata, decision, Tenant, Caller).Parameters["@cursor_0"].ShouldBe(7);
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
        var request = new BulkTableStreamRequest(
            Select: ["id", "status"],
            Limit: 50);
        var cursor = IssueCursor(new Dictionary<string, object?> { ["id"] = 100 }, metadata, request);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, metadata, decision, Tenant, Caller);

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
        var request = new BulkTableStreamRequest(Limit: 1000);
        var cursor = IssueCursor(new Dictionary<string, object?>
        {
            ["tenant_id"] = "corp",
            ["id"] = 500
        }, metadata, request);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, metadata, decision, Tenant, Caller);

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
            Limit: 100);
        var cursor = IssueCursor(new Dictionary<string, object?> { ["id"] = 200 }, metadata, request);

        var planned = planner.PlanKeysetStream(request with { Cursor = cursor }, metadata, decision, Tenant, Caller);

        planned.Sql.ShouldContain("WHERE (\"status\" = @p0 AND \"amount\" > @p1) AND (\"id\" > @cursor_0)");
        planned.Parameters["@p0"].ShouldBe("completed");
        planned.Parameters["@p1"].ShouldBe(50.0);
        planned.Parameters["@cursor_0"].ShouldBe(200);
    }

    [Fact]
    public void PlanKeysetStream_WithActiveRlsPolicy_DoesNotSplicePolicyText()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var decision = CreateAllowedDecision(metadata.Identifier, rowFilterSql: "\"tenant_id\" = 'tenant_42'");

        using var statusJson = JsonDocument.Parse("\"active\"");
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "status", Op: "eq", Value: statusJson.RootElement),
            Limit: 50);

        var planned = planner.PlanKeysetStream(request, metadata, decision);

        // VF-06: the governed rewriter applies the row policy once; the planner never splices policy text.
        planned.Sql.ShouldContain("WHERE (\"status\" = @p0)");
        planned.Sql.ShouldNotContain("tenant_42");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PlanKeysetStream_NotInWithEmptyList_DoesNotSatisfyARequiredFilterColumn()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(requiredFilters: ["status"]);
        var decision = CreateAllowedDecision(metadata.Identifier);

        // {op: notin, value: []} renders "1 = 1" and constrains nothing.
        using var empty = JsonDocument.Parse("[]");
        var request = new BulkTableStreamRequest(
            Filter: new TypedFilterNode(Field: "status", Op: "notin", Value: empty.RootElement));

        Should.Throw<RequiredFilterColumnMissingException>(() => planner.PlanKeysetStream(request, metadata, decision));
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
