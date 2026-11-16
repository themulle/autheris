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

/// <summary>REQ-SQL-128 WP-4: pre-execution validation, transport refusal and audit field unit tests using a recording fake connection.</summary>
public sealed class ClientListWebSqlTests
{
    private static IReadOnlyDictionary<string, object?> P(string name, string json) =>
        new Dictionary<string, object?> { [name] = Json(json) };

    [Fact]
    public async Task WebSql_TooManyListParameters_IsRefused()
    {
        await using var f = await CreateAsync(maxLists: 2);
        var parameters = new Dictionary<string, object?> { ["a"] = Json("[1]"), ["b"] = Json("[2]"), ["c"] = Json("[3]") };
        var ex = await Should.ThrowAsync<ClientListException>(() => f.IdsAsync("SELECT id FROM orders WHERE id IN (@a)", parameters));
        ex.Code.ShouldBe("SQL_LIST_PARAM_TOO_LARGE");
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

    // SEC-128-05 / M-8
    [Fact]
    [Trait("Category", "Security")]
    public async Task ListValue_OnPlannedPath_OnOtherTransports_AndNullTransport_IsRefused()
    {
        await using var f = await CreateAsync();
        const string sql = "SELECT id FROM orders WHERE id IN (@ids)";
        var raw = P("ids", "[1,2]");
        foreach (string? transport in new string?[] { null, SqlTransports.Rest, SqlTransports.Mcp, SqlTransports.ArrowExport, SqlTransports.AsyncJob, SqlTransports.Olap, "graphql" })
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
