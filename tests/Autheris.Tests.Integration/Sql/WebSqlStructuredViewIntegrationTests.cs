namespace Autheris.Tests.Integration.Sql;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.View;
using Autheris.Domain.Common;
using Autheris.Tests.Common;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Integration.Sql.ClientListPgFixture;

/// <summary>The structured WebSQL view through the whole governed pipeline against PostgreSQL: the stored statement decides, the view only narrows and sorts.</summary>
public sealed class WebSqlStructuredViewIntegrationTests
{
    private const string Stored = "SELECT id, status, amount, created FROM orders WHERE amount >= @min";

    private static Dictionary<string, object?> Min(int value) => new() { ["min"] = Json(value.ToString(CultureInfo.InvariantCulture)) };

    private static GovernedSqlQueryRequest Request(string stored, WebSqlViewPlan plan, IReadOnlyDictionary<string, object?>? parameters, SqlPageWindow? page = null, string? next = null) =>
        plan.Apply(new GovernedSqlQueryRequest(stored, parameters, Transport: SqlTransports.WebSql, Page: page, NextToken: next));

    private static async Task<GovernedSqlResult> RunAsync(
        ClientListPgFixture f, string view, string tenant = TenantA, string stored = Stored, IReadOnlyDictionary<string, object?>? parameters = null,
        SqlPageWindow? page = null, string? next = null) =>
        await f.Service.ExecuteQueryBufferedAsync(
            Request(stored, WebSqlViewComposer.Plan(stored, Json(view)), parameters ?? Min(0), page, next), User(tenant), new TenantId(tenant));

    private static long[] Ids(GovernedSqlResult result) => result.Rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture)).ToArray();

    [RequiresContainerRuntimeFact]
    public async Task Filter_and_sort_return_the_expected_rows()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        var result = await RunAsync(f, """
            { "filter": { "and": [
                { "column": "status", "op": "in", "values": ["new", "paid"] },
                { "column": "amount", "op": "gte", "value": 20 } ] },
              "orderBy": [ { "column": "amount", "direction": "desc" } ] }
            """);

        Ids(result).ShouldBe([4, 3, 2]);
    }

    [RequiresContainerRuntimeFact]
    public async Task The_stored_statements_own_parameters_and_the_view_combine()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "eq", "value": "paid" }, "orderBy": [ { "column": "id" } ] }""", parameters: Min(40)))
            .ShouldBe([4]);
        Ids(await RunAsync(f, """{ "filter": { "or": [ { "column": "status", "op": "eq", "value": "void" }, { "not": { "column": "amount", "op": "lt", "value": 30 } } ] }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([3, 4, 5]);
        Ids(await RunAsync(f, """{ "filter": { "column": "amount", "op": "between", "values": [20, 40] }, "orderBy": [ { "column": "id", "direction": "desc" } ] }"""))
            .ShouldBe([4, 3, 2]);
        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "notIn", "values": ["new"] }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([3, 4, 5]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Text_matching_is_literal_and_wildcards_in_the_value_do_not_match()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "contains", "value": "ai" }, "orderBy": [ { "column": "id" } ] }""")).ShouldBe([3, 4]);
        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "startsWith", "value": "ne" }, "orderBy": [ { "column": "id" } ] }""")).ShouldBe([1, 2]);
        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "endsWith", "value": "id" }, "orderBy": [ { "column": "id" } ] }""")).ShouldBe([3, 4, 5]);

        // counter-check: the same text without the wildcard characters matches, with them nothing does
        foreach (var wildcard in new[] { "%", "_", "n%", "%w", "ne_", "n_w" })
        {
            Ids(await RunAsync(f, $$"""{ "filter": { "column": "status", "op": "contains", "value": "{{wildcard}}" } }""")).ShouldBeEmpty();
        }
    }

    [RequiresContainerRuntimeFact]
    public async Task A_timestamp_column_can_be_filtered_with_an_iso_string()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        Ids(await RunAsync(f, """{ "filter": { "column": "created", "op": "gte", "value": "2026-01-04T00:00:00" }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([3, 4, 5]);
        Ids(await RunAsync(f, """{ "filter": { "column": "created", "op": "gt", "value": "2026-01-04T00:00:00" }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([4, 5]);
        Ids(await RunAsync(f, """{ "filter": { "column": "created", "op": "between", "values": ["2026-01-02", "2026-01-03T12:00:00"] }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([1, 2]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task The_row_rule_of_the_statement_applies_with_any_view()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        // tenant_b rows exist (ids 6 and 7) but tenant_a never sees them, whatever the filter asks for
        Ids(await RunAsync(f, """{ "filter": { "column": "id", "op": "in", "values": [6, 7] } }""")).ShouldBeEmpty();
        Ids(await RunAsync(f, """{ "filter": { "not": { "column": "id", "op": "in", "values": [1, 2, 3, 4, 5] } } }""")).ShouldBeEmpty();
        Ids(await RunAsync(f, """{ "filter": { "or": [ { "column": "status", "op": "eq", "value": "paid" }, { "column": "id", "op": "isNotNull" } ] }, "orderBy": [ { "column": "id" } ] }"""))
            .ShouldBe([1, 2, 3, 4, 5]);

        // counter-check: the other tenant reads its own rows with the same view
        Ids(await RunAsync(f, """{ "filter": { "column": "id", "op": "in", "values": [6, 7] }, "orderBy": [ { "column": "id" } ] }""", TenantB)).ShouldBe([6, 7]);
    }

    [RequiresContainerRuntimeFact]
    public async Task Total_count_counts_the_filtered_rows_without_the_page_window()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        var plan = WebSqlViewComposer.Plan(Stored, Json("""{ "filter": { "column": "status", "op": "in", "values": ["new", "paid"] }, "orderBy": [ { "column": "id" } ], "totalCount": true }"""));
        var countRequest = plan.ApplyCount(new GovernedSqlQueryRequest(Stored, Min(0), Transport: SqlTransports.WebSql, Page: new SqlPageWindow(0, 2, 1)))!;

        var count = await f.Service.ExecuteQueryBufferedAsync(countRequest, User(TenantA), new TenantId(TenantA));

        Convert.ToInt64(count.Rows.Single().Values.Single(), CultureInfo.InvariantCulture).ShouldBe(4);

        // the tenant rule applies to the count as well
        var other = await f.Service.ExecuteQueryBufferedAsync(countRequest, User(TenantB), new TenantId(TenantB));
        Convert.ToInt64(other.Rows.Single().Values.Single(), CultureInfo.InvariantCulture).ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task A_page_token_continues_only_under_the_same_view_values()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        const string view = """{ "filter": { "column": "amount", "op": "gte", "value": 10 }, "orderBy": [ { "column": "id" } ] }""";
        var first = await RunAsync(f, view, page: new SqlPageWindow(0, 2, 1));
        Ids(first).ShouldBe([1, 2]);
        first.Next.ShouldNotBeNull();

        var second = await RunAsync(f, view, next: first.Next);
        Ids(second).ShouldBe([3, 4]);

        // another filter value, another sort, and another stored parameter never continue this page
        const string otherValue = """{ "filter": { "column": "amount", "op": "gte", "value": 11 }, "orderBy": [ { "column": "id" } ] }""";
        await Should.ThrowAsync<SqlPagingTokenInvalidException>(() => RunAsync(f, otherValue, next: first.Next));
        const string otherSort = """{ "filter": { "column": "amount", "op": "gte", "value": 10 }, "orderBy": [ { "column": "id", "direction": "desc" } ] }""";
        await Should.ThrowAsync<SqlPagingTokenInvalidException>(() => RunAsync(f, otherSort, next: first.Next));
        await Should.ThrowAsync<SqlPagingTokenInvalidException>(() => RunAsync(f, view, parameters: Min(1), next: first.Next));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task No_filter_value_reaches_the_audit_record()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        const string secretValue = "zq-needle-8841";
        await RunAsync(f, $$"""{ "filter": { "or": [ { "column": "status", "op": "eq", "value": "{{secretValue}}" }, { "column": "status", "op": "contains", "value": "{{secretValue}}" } ] } }""");

        f.Audit.ShouldNotBeEmpty();
        foreach (var entry in f.Audit)
        {
            (entry.Query ?? string.Empty).ShouldNotContain(secretValue);
            entry.DetailsJson.ShouldNotContain(secretValue);
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Injection_text_in_a_value_is_data()
    {
        await using var f = await CreateAsync();
        if (f is null) return;

        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "eq", "value": "new' OR 1=1 --" } }""")).ShouldBeEmpty();
        Ids(await RunAsync(f, """{ "filter": { "column": "status", "op": "in", "values": ["x') OR ('1'='1", "void"] } }""")).ShouldBe([5]);
        (await f.ScalarAsync("SELECT COUNT(*) FROM public.orders")).ShouldBe(7);
    }
}
