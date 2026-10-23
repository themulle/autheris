namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Sql;
using Shouldly;
using Xunit;

/// <summary>
/// VF-06: <see cref="GovernedTableReadPlanner.PlanKeysetStream"/> no longer splices row-policy text into the SQL it plans; the governed
/// rewriter (<c>PlannedTableRead: true</c>) is the single place that applies tenant RLS, consent/access-profile row filters and Virtual Filters.
/// These tests carry the tenant guarantee that T_SEC_DATA178_06 (BulkKeysetStreamingSecurityTests) used to assert as planner SQL text.
/// </summary>
public sealed class BulkKeysetPlannedReadTenantSecurityTests
{
    private static TableMetadata Orders() => new()
    {
        Identifier = new TableIdentifier("default", "main", "orders"),
        Table = new Table { TableName = "orders", SchemaName = "main", SourceName = string.Empty, SourceType = "Sqlite" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "status", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static PlannedTableRead PlanWithCursor(long afterId)
    {
        var metadata = Orders();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        var encoder = new KeysetCursorEncoder();
        var first = new BulkTableStreamRequest();
        var context = KeysetCursorContextFactory.ForBulkStream(ClientListFixture.TenantA, metadata, metadata.PrimaryKeyColumns, "caller", first);
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = afterId }, context);
        return new GovernedTableReadPlanner().PlanKeysetStream(first with { Cursor = cursor }, metadata, decision, ClientListFixture.TenantA, "caller");
    }

    // Companion of T_SEC_DATA178_06 (BulkKeysetStreamingSecurityTests): the planner SQL is only the client part (cursor seek AND-conjoined,
    // no policy text); this test proves the tenant predicate is applied to that exact SQL by the governed pipeline.
    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0, new long[] { 1, 2, 3, 4, 5 })] // the cursor is below tenant_b's ids 6 and 7: they must still not come back
    [InlineData(3, new long[] { 4, 5 })]
    [InlineData(5, new long[0])] // the next ids (6, 7) belong to tenant_b
    public async Task T_SEC_DATA178_06b_KeysetPlannedRead_TenantPredicateAppliedByRewriter(long afterId, long[] expectedIds)
    {
        await using var f = await ClientListFixture.CreateAsync();
        var planned = PlanWithCursor(afterId);

        planned.Sql.ShouldContain("(\"id\" > @cursor_0)");
        planned.Sql.ShouldContain("WHERE (\"id\" > @cursor_0) ORDER BY"); // no policy text in the WHERE; the rewriter must add the tenant predicate
        planned.Sql.ShouldNotContain("tenant_a", Case.Insensitive);
        planned.Parameters.ShouldContainKey("@cursor_0");

        var result = await f.QueryAsync(planned.Sql, planned.Parameters, ClientListFixture.TenantA, SqlTransports.Rest, planned: true);

        result.Rows.Select(r => Convert.ToInt64(r["id"], System.Globalization.CultureInfo.InvariantCulture)).ToArray().ShouldBe(expectedIds);
        result.Rows.ShouldAllBe(r => (string?)r["tenant_id"] == ClientListFixture.TenantA);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA178_06b_KeysetPlannedRead_OtherTenantCaller_SeesOnlyOwnRows()
    {
        await using var f = await ClientListFixture.CreateAsync();
        var planned = PlanWithCursor(0);

        var result = await f.QueryAsync(planned.Sql, planned.Parameters, ClientListFixture.TenantB, SqlTransports.Rest, planned: true);

        result.Rows.Select(r => Convert.ToInt64(r["id"], System.Globalization.CultureInfo.InvariantCulture)).ToArray().ShouldBe([6L, 7L]);
    }
}
