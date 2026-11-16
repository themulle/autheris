namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Integration.Sql;
using Shouldly;
using Xunit;

/// <summary>
/// VF-06: <see cref="GovernedTableReadPlanner.PlanKeysetStream"/> no longer splices row-policy text into the SQL it plans; the governed
/// rewriter (<c>PlannedTableRead: true</c>) is the single place that applies tenant RLS, consent/access-profile row filters and Virtual Filters.
/// </summary>
[Trait("Category", "Security")]
public sealed class BulkKeysetPlannedReadTenantSecurityIntegrationTests
{
    private static TableMetadata Orders() => new()
    {
        Identifier = new TableIdentifier("default", "public", "orders"),
        Table = new Table { TableName = "orders", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql" },
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
        var encoder = new KeysetCursorEncoder("bulk-keyset-tenant-security-key-0123456789");
        var first = new BulkTableStreamRequest();
        var context = KeysetCursorContextFactory.ForBulkStream(ClientListPgFixture.TenantA, metadata, metadata.PrimaryKeyColumns, "caller", first);
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = afterId }, context);
        return new GovernedTableReadPlanner(encoder).PlanKeysetStream(first with { Cursor = cursor }, metadata, decision, ClientListPgFixture.TenantA, "caller");
    }

    [RequiresContainerRuntimeTheory]
    [Trait("Category", "Security")]
    [InlineData(0, new long[] { 1, 2, 3, 4, 5 })]
    [InlineData(3, new long[] { 4, 5 })]
    [InlineData(5, new long[0])]
    public async Task T_SEC_DATA178_06b_KeysetPlannedRead_TenantPredicateAppliedByRewriter(long afterId, long[] expectedIds)
    {
        await using var f = await ClientListPgFixture.CreateAsync();
        if (f is null) return;
        var planned = PlanWithCursor(afterId);

        planned.Sql.ShouldContain("(\"id\" > @cursor_0)");
        planned.Sql.ShouldContain("WHERE (\"id\" > @cursor_0) ORDER BY");
        planned.Sql.ShouldNotContain("tenant_a", Case.Insensitive);
        planned.Parameters.ShouldContainKey("@cursor_0");

        var result = await f.QueryAsync(planned.Sql, planned.Parameters, ClientListPgFixture.TenantA, SqlTransports.Rest, planned: true);

        result.Rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture)).ToArray().ShouldBe(expectedIds);
        result.Rows.ShouldAllBe(r => (string?)r["tenant_id"] == ClientListPgFixture.TenantA);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA178_06b_KeysetPlannedRead_OtherTenantCaller_SeesOnlyOwnRows()
    {
        await using var f = await ClientListPgFixture.CreateAsync();
        if (f is null) return;
        var planned = PlanWithCursor(0);

        var result = await f.QueryAsync(planned.Sql, planned.Parameters, ClientListPgFixture.TenantB, SqlTransports.Rest, planned: true);

        result.Rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture)).ToArray().ShouldBe([6L, 7L]);
    }
}
