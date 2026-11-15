namespace Autheris.Tests.Integration.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Tests.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Integration.Sql.ClientListPgFixture;

/// <summary>REQ-SQL-128 WP-4: list parameters through the whole governed pipeline against PostgreSQL.</summary>
public sealed class ClientListWebSqlIntegrationTests
{
    private static IReadOnlyDictionary<string, object?> P(string name, string json) =>
        new Dictionary<string, object?> { [name] = Json(json) };

    [RequiresContainerRuntimeFact]
    public async Task WebSql_ListQuery_ReturnsRowsForThreeValues()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", P("ids", "[1,3,5]"))).ShouldBe([1, 3, 5]);
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_OneValue_Duplicates_AndEmpty()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        (await f.IdsAsync(sql, P("ids", "[2]"))).ShouldBe([2]);
        (await f.IdsAsync(sql, P("ids", "[2,2,2,4,4]"))).ShouldBe([2, 4]);
        (await f.IdsAsync(sql, P("ids", "[]"))).ShouldBeEmpty();
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[]"))).ShouldBe([1, 2, 3, 4, 5]);
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[1,2]"))).ShouldBe([3, 4, 5]);
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_StringAndMixedCaseNames_AndAtPrefixedKey()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        (await f.IdsAsync("SELECT id FROM orders WHERE status IN (@Statuses) ORDER BY id", P("statuses", "[\"paid\",\"void\"]"))).ShouldBe([3, 4, 5]);
        (await f.IdsAsync("SELECT id FROM orders WHERE status IN (@s) ORDER BY id", P("@s", "[\"new\"]"))).ShouldBe([1, 2]);
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@Ids) OR id IN (@ids) ORDER BY id", P("IDS", "[1,5]"))).ShouldBe([1, 5]);
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_ScalarParametersStillWork_NextToLists()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        var parameters = new Dictionary<string, object?> { ["ids"] = Json("[1,2,3,4]"), ["minAmount"] = Json("25") };
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) AND amount > @minAmount ORDER BY id", parameters)).ShouldBe([3, 4]);
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@one) ORDER BY id", P("one", "7"))).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_AtTheConfiguredMaximum_IsAccepted_AboveIsRefused_NeverTruncated()
    {
        await using var f = await CreateAsync(maxListValues: 50);
        if (f is null) return;
        string Many(int n) => "[" + string.Join(",", Enumerable.Range(1, n)) + "]";
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", Many(50)))).Length.ShouldBe(5);
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", Many(51))));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_DuplicatesAreRemovedBeforeTheMaximumIsChecked()
    {
        await using var f = await CreateAsync(maxListValues: 3);
        if (f is null) return;
        string json = "[" + string.Join(",", Enumerable.Repeat("1", 28)) + ",2,3]";
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", P("ids", json))).ShouldBe([1, 2, 3]);
    }

    [RequiresContainerRuntimeFact]
    public async Task WebSql_TheDefaultMaximum_OfOneThousand_IsAccepted_OnPostgres()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        string json = "[" + string.Join(",", Enumerable.Range(1, 1000)) + "]";
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", json))).Length.ShouldBe(5);
        string tooMany = "[" + string.Join(",", Enumerable.Range(1, 1001)) + "]";
        await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", tooMany)));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task UnreferencedList_IsDropped_AndDoesNotChangeTheOutcome_SEC_128_04()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        var parameters = new Dictionary<string, object?> { ["ids"] = Json("[1,2]"), ["unused"] = Json("[1,2,3,4,5,6,7,8,9]") };
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", parameters)).ShouldBe([1, 2]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task WebSql_ValuesWithQuotesCommentsSeparators_NeverAppearInSqlAuditOrLogs()
    {
        var logger = new PgListCapturingLogger<Autheris.Application.Sql.Services.GovernedSqlExecutionService>();
        await using var f = await CreateAsync(logger: logger);
        if (f is null) return;
        string[] hostile = ["x' OR 1=1 --", "a;DROP TABLE orders", "/* c */", "new\"; --", "paid') UNION SELECT 1 --"];
        string json = "[" + string.Join(",", hostile.Select(h => JsonSerializer.Serialize(h))) + "]";

        var rows = await f.IdsAsync("SELECT id FROM orders WHERE status IN (@s)", P("s", json));
        rows.ShouldBeEmpty();
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);

        var text = string.Join("\n", f.Audit.Select(e => e.DetailsJson)) + "\n" + string.Join("\n", logger.Entries);
        foreach (string value in hostile)
        {
            text.ShouldNotContain(value);
        }

        text.ShouldNotContain("DROP TABLE");
        f.Audit.Single(e => e.EventType == "WEBSQL_QUERY").DetailsJson.ShouldContain("\"list_params\":{\"s\":5}");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task TenantScope_AppliesUnchanged_ToEveryValueOfTheList()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        (await f.IdsAsync(sql, P("ids", "[1,6,7]"), tenant: TenantA)).ShouldBe([1]);
        (await f.IdsAsync(sql, P("ids", "[1,6,7]"), tenant: TenantB)).ShouldBe([6, 7]);
        (await f.IdsAsync(sql, P("ids", "[]"), tenant: TenantB)).ShouldBeEmpty();
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[]"), tenant: TenantB)).ShouldBe([6, 7]);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("DELETE FROM orders WHERE id NOT IN (@e)")]
    [InlineData("DELETE FROM orders WHERE amount = 10 OR id NOT IN (@e)")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id NOT IN (@e)")]
    [InlineData("DELETE FROM orders WHERE id IN (@e)")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id IN (@e)")]
    [Trait("Category", "Security")]
    public async Task Dml_EmptyList_IsRejectedAsUnfiltered_NoRowsAffected(string sql)
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        await Should.ThrowAsync<UnfilteredDmlException>(() => f.ExecuteAsync(sql, P("e", "[]")));
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);
        f.Audit.ShouldContain(e => e.EventType == "WEBSQL_DML_REJECTED");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task CheckedDml_ListBinding_DeletesExactlyTheListedRows_OfTheTenant()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        await f.ExecuteAsync("DELETE FROM orders WHERE id IN (@ids)", P("ids", "[1,2,6]"));
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(5);
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE tenant_id = 'tenant_b'")).ShouldBe(2);

        await f.ExecuteAsync("UPDATE orders SET amount = 1 WHERE id IN (@ids)", P("ids", "[3,4]"));
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 1")).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public async Task ClientListValue_OnSqlEndpoints_IsAdmitted_AndRevalidated()
    {
        await using var f = await CreateAsync(maxListValues: 3);
        if (f is null) return;
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        var ok = new Dictionary<string, object?> { ["ids"] = ClientListValue.Create([1L, 2L], SqlParameterType.Int64) };
        (await f.QueryAsync(sql, ok, transport: SqlTransports.SqlEndpoints)).Rows.Count.ShouldBe(2);

        var tooBig = new Dictionary<string, object?> { ["ids"] = ClientListValue.Create([1L, 2L, 3L, 4L], SqlParameterType.Int64) };
        (await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, tooBig, transport: SqlTransports.SqlEndpoints))).Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task PlannedTableRead_WithScalarParameters_IsUnchanged()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        var result = await f.QueryAsync("SELECT id FROM orders WHERE id = @pid", new Dictionary<string, object?> { ["pid"] = 2L }, transport: SqlTransports.Rest, planned: true);
        result.Rows.Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Trino_Route_AcceptsAListParameter_ThroughTheSharedExecutor()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        var parameters = new Dictionary<string, object?> { ["ids"] = Json("[1,2,6]") };
        var result = await f.QueryAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", parameters, transport: SqlTransports.Trino);
        result.Rows.Count.ShouldBe(2);
    }
}
