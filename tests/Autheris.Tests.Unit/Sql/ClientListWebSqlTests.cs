namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Unit.Sql.ClientListFixture;

/// <summary>REQ-SQL-128 WP-4: list parameters through the whole governed pipeline against SQLite (AC-128-1, AC-128-2, AC-128-5, AC-128-6).</summary>
public sealed class ClientListWebSqlTests
{
    private static IReadOnlyDictionary<string, object?> P(string name, string json) =>
        new Dictionary<string, object?> { [name] = Json(json) };

    [Fact]
    public async Task WebSql_ListQuery_ReturnsRowsForThreeValues()
    {
        await using var f = await CreateAsync();
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", P("ids", "[1,3,5]"))).ShouldBe([1, 3, 5]);
    }

    [Fact]
    public async Task WebSql_OneValue_Duplicates_AndEmpty()
    {
        await using var f = await CreateAsync();
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        (await f.IdsAsync(sql, P("ids", "[2]"))).ShouldBe([2]);
        (await f.IdsAsync(sql, P("ids", "[2,2,2,4,4]"))).ShouldBe([2, 4]);
        (await f.IdsAsync(sql, P("ids", "[]"))).ShouldBeEmpty();
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[]"))).ShouldBe([1, 2, 3, 4, 5]);
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[1,2]"))).ShouldBe([3, 4, 5]);
    }

    [Fact]
    public async Task WebSql_StringAndMixedCaseNames_AndAtPrefixedKey()
    {
        await using var f = await CreateAsync();
        (await f.IdsAsync("SELECT id FROM orders WHERE status IN (@Statuses) ORDER BY id", P("statuses", "[\"paid\",\"void\"]"))).ShouldBe([3, 4, 5]);
        (await f.IdsAsync("SELECT id FROM orders WHERE status IN (@s) ORDER BY id", P("@s", "[\"new\"]"))).ShouldBe([1, 2]);
        // M-11: a statement that spells one list two ways reads the same value for both spellings
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@Ids) OR id IN (@ids) ORDER BY id", P("IDS", "[1,5]"))).ShouldBe([1, 5]);
    }

    [Fact]
    public async Task WebSql_ScalarParametersStillWork_NextToLists()
    {
        await using var f = await CreateAsync();
        var parameters = new Dictionary<string, object?> { ["ids"] = Json("[1,2,3,4]"), ["minAmount"] = Json("25") };
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) AND amount > @minAmount ORDER BY id", parameters)).ShouldBe([3, 4]);
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@one) ORDER BY id", P("one", "7"))).ShouldBeEmpty();   // tenant_b's row: scalar IN stays scalar
    }

    [Fact]
    public async Task WebSql_AtTheConfiguredMaximum_IsAccepted_AboveIsRefused_NeverTruncated()
    {
        await using var f = await CreateAsync(maxListValues: 50);
        string Many(int n) => "[" + string.Join(",", Enumerable.Range(1, n)) + "]";
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", Many(50)))).Length.ShouldBe(5);
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", Many(51))));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [Fact]
    public async Task WebSql_DuplicatesAreRemovedBeforeTheMaximumIsChecked()
    {
        await using var f = await CreateAsync(maxListValues: 3);
        string json = "[" + string.Join(",", Enumerable.Repeat("1", 28)) + ",2,3]";   // 30 raw elements: the raw ceiling is 10 x the maximum
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", P("ids", json))).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task WebSql_TheDefaultMaximum_OfOneThousand_IsAccepted_OnSqlite()
    {
        await using var f = await CreateAsync();
        string json = "[" + string.Join(",", Enumerable.Range(1, 1000)) + "]";
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", json))).Length.ShouldBe(5);
        string tooMany = "[" + string.Join(",", Enumerable.Range(1, 1001)) + "]";
        await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", tooMany)));
    }

    [Fact]
    public async Task WebSql_TooManyListParameters_IsRefused()
    {
        await using var f = await CreateAsync(maxLists: 2);
        var parameters = new Dictionary<string, object?> { ["a"] = Json("[1]"), ["b"] = Json("[2]"), ["c"] = Json("[3]") };
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@a)", parameters));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnreferencedList_IsDropped_AndDoesNotChangeTheOutcome_SEC_128_04()
    {
        await using var f = await CreateAsync();
        var parameters = new Dictionary<string, object?> { ["ids"] = Json("[1,2]"), ["unused"] = Json("[1,2,3,4,5,6,7,8,9]") };
        (await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) ORDER BY id", parameters)).ShouldBe([1, 2]);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE id = @l")]
    [InlineData("SELECT id FROM orders WHERE id IN (@l, 1)")]
    [InlineData("SELECT id FROM orders WHERE @l IN (1, 2)")]
    [InlineData("SELECT id FROM orders WHERE id > ABS(@l)")]
    [Trait("Category", "Security")]
    public async Task List_InAnyOtherPosition_IsRefusedWithAStableCode(string sql)
    {
        await using var f = await CreateAsync();
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync(sql, P("l", "[1,2]")));
        ex.Code.ShouldBe("SQL_LIST_PARAM_POSITION");
        f.Audit.ShouldContain(e => e.EventType == "WEBSQL_QUERY_DENIED" && e.DetailsJson!.Contains("SQL_LIST_PARAM_POSITION"));
    }

    [Theory]
    [InlineData("[1, null]", "SQL_LIST_PARAM_NULL")]
    [InlineData("[1, \"a\"]", "SQL_LIST_PARAM_TYPE")]
    [InlineData("[[1]]", "SQL_LIST_PARAM_TYPE")]
    [Trait("Category", "Security")]
    public async Task InvalidElements_AreRefused_BeforeAnythingRuns(string json, string code)
    {
        await using var f = await CreateAsync();
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", P("ids", json)));
        ex.Code.ShouldBe(code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task List_AsLimit_IsRefused_NothingRuns()
    {
        await using var f = await CreateAsync();
        await Should.ThrowAsync<Exception>(() => f.IdsAsync("SELECT id FROM orders LIMIT @l", P("l", "[1,2]")));
    }

    [Fact]
    public async Task ScalarParameter_StillRejectsObjects_AsBefore()
    {
        await using var f = await CreateAsync();
        await Should.ThrowAsync<ArgumentException>(() => f.IdsAsync("SELECT id FROM orders WHERE id = @x", P("x", "{\"a\":1}")));
    }

    // AC-128-5 / V-128-1 (plan test 18)
    [Fact]
    [Trait("Category", "Security")]
    public async Task WebSql_ValuesWithQuotesCommentsSeparators_NeverAppearInSqlAuditOrLogs()
    {
        var logger = new ListCapturingLogger<Autheris.Application.Sql.Services.GovernedSqlExecutionService>();
        await using var f = await CreateAsync(logger: logger);
        string[] hostile = ["x' OR 1=1 --", "a;DROP TABLE orders", "/* c */", "new\"; --", "paid') UNION SELECT 1 --"];
        string json = "[" + string.Join(",", hostile.Select(h => System.Text.Json.JsonSerializer.Serialize(h))) + "]";

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

    // AC-128-6 (plan test 19)
    [Fact]
    [Trait("Category", "Security")]
    public async Task TenantScope_AppliesUnchanged_ToEveryValueOfTheList()
    {
        await using var f = await CreateAsync();
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        (await f.IdsAsync(sql, P("ids", "[1,6,7]"), tenant: TenantA)).ShouldBe([1]);
        (await f.IdsAsync(sql, P("ids", "[1,6,7]"), tenant: TenantB)).ShouldBe([6, 7]);
        (await f.IdsAsync(sql, P("ids", "[]"), tenant: TenantB)).ShouldBeEmpty();
        (await f.IdsAsync("SELECT id FROM orders WHERE id NOT IN (@ids) ORDER BY id", P("ids", "[]"), tenant: TenantB)).ShouldBe([6, 7]);
    }

    // SEC-128-01 / M-1 (SQLite execution)
    [Theory]
    [InlineData("DELETE FROM orders WHERE id NOT IN (@e)")]
    [InlineData("DELETE FROM orders WHERE amount = 10 OR id NOT IN (@e)")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id NOT IN (@e)")]
    [InlineData("DELETE FROM orders WHERE id IN (@e)")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id IN (@e)")]
    [Trait("Category", "Security")]
    public async Task Dml_EmptyList_IsRejectedAsUnfiltered_NoRowsAffected(string sql)
    {
        await using var f = await CreateAsync();
        await Should.ThrowAsync<UnfilteredDmlException>(() => f.ExecuteAsync(sql, P("e", "[]")));
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);
        f.Audit.ShouldContain(e => e.EventType == "WEBSQL_DML_REJECTED");
    }

    // M-12
    [Fact]
    [Trait("Category", "Security")]
    public async Task CheckedDml_ListBinding_DeletesExactlyTheListedRows_OfTheTenant()
    {
        await using var f = await CreateAsync();
        await f.ExecuteAsync("DELETE FROM orders WHERE id IN (@ids)", P("ids", "[1,2,6]"));   // 6 belongs to tenant_b
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(5);
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE tenant_id = 'tenant_b'")).ShouldBe(2);

        await f.ExecuteAsync("UPDATE orders SET amount = 1 WHERE id IN (@ids)", P("ids", "[3,4]"));
        (await f.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 1")).ShouldBe(2);
    }

    // SEC-128-05 / M-8
    [Fact]
    [Trait("Category", "Security")]
    public async Task ListValue_OnPlannedPath_OnOtherTransports_AndNullTransport_IsRefused()
    {
        await using var f = await CreateAsync();
        const string sql = "SELECT id FROM orders WHERE id IN (@ids)";
        var raw = P("ids", "[1,2]");
        foreach (string? transport in new string?[] { null, SqlTransports.Rest, SqlTransports.Mcp, SqlTransports.ArrowExport, SqlTransports.FlightSql, SqlTransports.AsyncJob, SqlTransports.Olap, "graphql" })
        {
            var ex = await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, raw, transport: transport));
            ex.Code.ShouldBe("SQL_LIST_PARAM_POSITION");
        }

        // the planned-read flag refuses a list on an otherwise admitted transport
        var planned = await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, raw, transport: SqlTransports.WebSql, planned: true));
        planned.Code.ShouldBe("SQL_LIST_PARAM_POSITION");

        // other value shapes: a CLR collection on WebSql, a JsonElement on SqlEndpoints, a ClientListValue on WebSql
        var clr = new Dictionary<string, object?> { ["ids"] = new long[] { 1, 2 } };
        (await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, clr))).Code.ShouldBe("SQL_LIST_PARAM_POSITION");
        (await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, raw, transport: SqlTransports.SqlEndpoints))).Code.ShouldBe("SQL_LIST_PARAM_POSITION");
        var value = new Dictionary<string, object?> { ["ids"] = ClientListValue.Create([1L, 2L], SqlParameterType.Int64) };
        (await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, value, transport: SqlTransports.WebSql))).Code.ShouldBe("SQL_LIST_PARAM_POSITION");
    }

    [Fact]
    public async Task ClientListValue_OnSqlEndpoints_IsAdmitted_AndRevalidated()
    {
        await using var f = await CreateAsync(maxListValues: 3);
        const string sql = "SELECT id FROM orders WHERE id IN (@ids) ORDER BY id";
        var ok = new Dictionary<string, object?> { ["ids"] = ClientListValue.Create([1L, 2L], SqlParameterType.Int64) };
        (await f.QueryAsync(sql, ok, transport: SqlTransports.SqlEndpoints)).Rows.Count.ShouldBe(2);

        // the executor does not trust the endpoint layer: the configured maximum applies again
        var tooBig = new Dictionary<string, object?> { ["ids"] = ClientListValue.Create([1L, 2L, 3L, 4L], SqlParameterType.Int64) };
        (await Should.ThrowAsync<ClientListException>(() => f.QueryAsync(sql, tooBig, transport: SqlTransports.SqlEndpoints))).Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlannedTableRead_WithScalarParameters_IsUnchanged()
    {
        await using var f = await CreateAsync();
        var result = await f.QueryAsync("SELECT id FROM orders WHERE id = @pid", new Dictionary<string, object?> { ["pid"] = 2L }, transport: SqlTransports.Rest, planned: true);
        result.Rows.Count.ShouldBe(1);
    }

    // C-15 / M-17: the audit field holds names and counts only
    [Fact]
    [Trait("Category", "Security")]
    public async Task Audit_RecordsListNamesAndCounts_NoValues()
    {
        await using var f = await CreateAsync();
        await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) AND status IN (@statuses)",
            new Dictionary<string, object?> { ["ids"] = Json("[1,2,3]"), ["statuses"] = Json("[\"super-secret\", \"other\"]") });

        string json = f.Audit.Single(e => e.EventType == "WEBSQL_QUERY").DetailsJson!;
        json.ShouldContain("\"list_params\":{\"ids\":3,\"statuses\":2}");
        json.ShouldNotContain("super-secret");
        json.ShouldNotContain("other");
    }

    // An empty list has no binder slot, so list_params cannot show it; list_params_empty names it (no values).
    [Fact]
    [Trait("Category", "Security")]
    public async Task Audit_EmptyList_LeavesACountOnlyTrace()
    {
        await using var f = await CreateAsync();
        await f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids) AND status IN (@statuses)",
            new Dictionary<string, object?> { ["ids"] = Json("[]"), ["statuses"] = Json("[\"super-secret\"]") });

        string json = f.Audit.Single(e => e.EventType == "WEBSQL_QUERY").DetailsJson!;
        json.ShouldContain("\"list_params_empty\":[\"ids\"]");
        json.ShouldContain("\"list_params\":{\"statuses\":1}");
        json.ShouldNotContain("super-secret");
    }

    [Fact]
    public async Task Audit_WithoutLists_HasNoListParamsField()
    {
        await using var f = await CreateAsync();
        await f.IdsAsync("SELECT id FROM orders WHERE id = @x", new Dictionary<string, object?> { ["x"] = Json("1") });
        f.Audit.Single(e => e.EventType == "WEBSQL_QUERY").DetailsJson!.ShouldNotContain("list_params");
    }
}
