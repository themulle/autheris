using System.Security;
using System.Text.Json;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.Sql.View;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql.View;

public class WebSqlViewComposerTests
{
    private const string Stored = "SELECT auftrag, kunde, werk, betrag, datum FROM erp.sales.auftraege WHERE datum >= @ab";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static WebSqlViewPlan Plan(string view, string stored = Stored) => WebSqlViewComposer.Plan(stored, Json(view));

    private static WebSqlViewException Refused(string view, string stored = Stored) =>
        Should.Throw<WebSqlViewException>(() => Plan(view, stored));

    [Fact]
    public void Grid_example_wraps_the_statement_with_bound_values_only()
    {
        var plan = Plan("""
            { "filter": { "and": [
                { "column": "werk", "op": "in", "values": ["Ehingen", "Nenzing"] },
                { "column": "kunde", "op": "contains", "value": "bau" },
                { "column": "betrag", "op": "gte", "value": 10000 } ] },
              "orderBy": [ { "column": "betrag", "direction": "desc" } ],
              "totalCount": true }
            """);

        plan.Sql.ShouldStartWith("SELECT * FROM (\n" + Stored + "\n) AS v WHERE ");
        plan.Sql.ShouldContain("v.werk IN (@__gql_tf_0, @__gql_tf_1)");
        plan.Sql.ShouldContain("v.kunde LIKE @__gql_tf_2 ESCAPE '!'");
        plan.Sql.ShouldContain("v.betrag >= @__gql_tf_3");
        plan.Sql.ShouldEndWith(" ORDER BY v.betrag DESC");
        plan.Sql.ShouldNotContain("Ehingen");
        plan.Sql.ShouldNotContain("bau");
        plan.Sql.ShouldNotContain("10000");
        plan.CountSql!.ShouldStartWith("SELECT COUNT(*) AS total_count FROM (\n" + Stored);
        plan.CountSql.ShouldNotContain("ORDER BY");
    }

    [Fact]
    public void Values_are_typed_scalars_and_like_patterns_are_escaped()
    {
        var request = Plan("""
            { "filter": { "and": [
                { "column": "kunde", "op": "startsWith", "value": "50%_off!" },
                { "column": "betrag", "op": "between", "values": [1, 2.5] },
                { "column": "werk", "op": "eq", "value": true } ] } }
            """).Apply(new GovernedSqlQueryRequest(Stored));

        request.StructuredViewRead.ShouldBeTrue();
        var values = request.GatewayParameters!;
        values["@__gql_tf_0"].ShouldBe("50!%!_off!!%");
        values["@__gql_tf_1"].ShouldBe(1L);
        values["@__gql_tf_2"].ShouldBe(2.5m);
        values["@__gql_tf_3"].ShouldBe(true);
        GovernedSqlExecutionService.MergeGatewayParameters(request).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void A_gateway_map_on_a_plain_request_still_fails_closed()
    {
        var plain = new GovernedSqlQueryRequest(Stored) { GatewayParameters = new Dictionary<string, object?> { ["@__gql_tf_0"] = 1L } };

        Should.Throw<SecurityException>(() => GovernedSqlExecutionService.MergeGatewayParameters(plain));
    }

    [Theory]
    [InlineData("""{ "filter": { "column": "geheim", "op": "eq", "value": 1 } }""")]
    [InlineData("""{ "filter": { "column": "auftraege.betrag", "op": "eq", "value": 1 } }""")]
    [InlineData("""{ "filter": { "column": "betrag) OR 1=1 --", "op": "eq", "value": 1 } }""")]
    [InlineData("""{ "filter": { "column": "*", "op": "isNull" } }""")]
    [InlineData("""{ "orderBy": [ { "column": "geheim" } ] }""")]
    [InlineData("""{ "orderBy": [ { "column": "betrag; DROP TABLE x" } ] }""")]
    [Trait("Category", "Security")]
    public void A_column_that_is_not_an_output_column_refuses_the_view(string view)
    {
        Refused(view).Message.ShouldStartWith("The view names a column that is not an output column");

        // counter-check: every real output column is accepted in the same shapes
        foreach (var column in new[] { "auftrag", "kunde", "werk", "betrag", "datum", "WERK" })
        {
            Plan($$"""{ "filter": { "column": "{{column}}", "op": "isNull" }, "orderBy": [ { "column": "{{column}}" } ] }""");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void The_refusal_names_the_column_bounded_and_without_control_characters()
    {
        var ex = Refused("{ \"orderBy\": [ { \"column\": \"" + new string('x', 200) + "\\n\\u0007evil\" } ] }");

        ex.Message.Length.ShouldBeLessThan(200);
        ex.Message.ShouldNotContain("\n");
        ex.Message.ShouldNotContain("\u0007");
    }

    [Theory]
    [InlineData("SELECT * FROM erp.sales.auftraege")]
    [InlineData("SELECT t.* FROM erp.sales.auftraege t")]
    [InlineData("SELECT auftrag, betrag * 2 FROM erp.sales.auftraege")]
    [InlineData("SELECT auftrag, betrag AS auftrag FROM erp.sales.auftraege")]
    [InlineData("SELECT auftrag FROM erp.sales.auftraege ORDER BY auftrag")]
    [InlineData("VALUES (1, 2)")]
    [InlineData("INSERT INTO erp.sales.auftraege (auftrag) VALUES (1)")]
    [InlineData("DELETE FROM erp.sales.auftraege")]
    [InlineData("SELECT auftrag FROM erp.sales.auftraege; DROP TABLE x")]
    [InlineData("SELECT auftrag FROM erp.sales.auftraege WHERE kunde = @__gql_tf_0")]
    [InlineData("SELECT auftrag FROM erp.sales.auftraege WHERE kunde = @__param_x")]
    [Trait("Category", "Security")]
    public void A_statement_whose_output_cannot_be_proven_refuses_the_view(string stored)
    {
        Refused("""{ "filter": { "column": "auftrag", "op": "isNull" } }""", stored);
    }

    [Fact]
    public void Aliased_expressions_set_operations_and_a_limited_order_by_are_accepted()
    {
        Plan("""{ "orderBy": [ { "column": "summe" } ] }""", "SELECT werk, SUM(betrag) AS summe FROM erp.sales.auftraege GROUP BY werk");
        Plan("""{ "orderBy": [ { "column": "a" } ] }""", "SELECT a FROM erp.sales.x UNION ALL SELECT b FROM erp.sales.y");
        Plan("""{ "orderBy": [ { "column": "a" } ] }""", "SELECT a FROM erp.sales.x ORDER BY a LIMIT 10");
        Plan("""{ "orderBy": [ { "column": "a" } ] }""", "SELECT a FROM erp.sales.x; -- trailing comment");
    }

    [Theory]
    [InlineData("""{ "groupBy": ["werk"] }""")]
    [InlineData("""{ "aggregates": [] }""")]
    [InlineData("""{ "sql": "SELECT 1" }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "regex", "value": "a" } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "eq", "value": "a", "extra": 1 } }""")]
    [InlineData("""{ "filter": { "and": [] } }""")]
    [InlineData("""{ "filter": { "and": [ { "column": "werk", "op": "isNull" } ], "or": [ { "column": "werk", "op": "isNull" } ] } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "eq", "value": null } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "eq", "value": { "$ne": 1 } } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "eq", "value": [1] } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "in", "values": [] } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "in", "value": "a" } }""")]
    [InlineData("""{ "filter": { "column": "werk", "op": "in", "values": [null] } }""")]
    [InlineData("""{ "filter": { "column": "betrag", "op": "between", "values": [1] } }""")]
    [InlineData("""{ "filter": { "column": "betrag", "op": "contains", "value": 1 } }""")]
    [InlineData("""{ "filter": { "column": "betrag", "op": "isNull", "value": true } }""")]
    [InlineData("""{ "orderBy": [ { "column": "betrag", "direction": "sideways" } ] }""")]
    [InlineData("""{ "orderBy": [ { "column": "betrag" }, { "column": "BETRAG" } ] }""")]
    [InlineData("""{ "orderBy": [ { "column": "betrag", "nulls": "first" } ] }""")]
    [InlineData("""{ "totalCount": "yes" }""")]
    [InlineData("""[]""")]
    [Trait("Category", "Security")]
    public void Anything_outside_the_closed_model_refuses_the_view(string view)
    {
        Refused(view);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Caps_on_depth_conditions_list_size_string_length_and_sort_columns_hold()
    {
        string Nest(int depth) => depth == 0
            ? """{ "column": "werk", "op": "isNull" }"""
            : $$"""{ "not": {{Nest(depth - 1)}} }""";

        // counter-check: at the limit it is accepted
        Plan($$"""{ "filter": {{Nest(WebSqlViewComposer.MaxFilterDepth)}} }""");
        Refused($$"""{ "filter": {{Nest(WebSqlViewComposer.MaxFilterDepth + 1)}} }""");

        string Leaves(int n) => string.Join(",", Enumerable.Repeat("""{ "column": "werk", "op": "isNull" }""", n));
        Plan($$"""{ "filter": { "and": [ {{Leaves(WebSqlViewComposer.MaxFilterLeaves)}} ] } }""");
        Refused($$"""{ "filter": { "and": [ {{Leaves(WebSqlViewComposer.MaxFilterLeaves + 1)}} ] } }""");

        string List(int n) => string.Join(",", Enumerable.Range(0, n));
        Plan($$"""{ "filter": { "column": "betrag", "op": "in", "values": [ {{List(1000)}} ] } }""");
        Refused($$"""{ "filter": { "column": "betrag", "op": "in", "values": [ {{List(1001)}} ] } }""");

        Plan($$"""{ "filter": { "column": "kunde", "op": "eq", "value": "{{new string('a', WebSqlViewComposer.MaxStringValueLength)}}" } }""");
        Refused($$"""{ "filter": { "column": "kunde", "op": "eq", "value": "{{new string('a', WebSqlViewComposer.MaxStringValueLength + 1)}}" } }""");

        var sorts = new[] { "auftrag", "kunde", "werk", "betrag", "datum" };
        Plan($$"""{ "orderBy": [ {{string.Join(",", sorts.Select(c => $$"""{ "column": "{{c}}" }"""))}} ] }""");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Injection_strings_are_values_never_text()
    {
        var plan = Plan("""{ "filter": { "column": "kunde", "op": "eq", "value": "x' OR '1'='1" } }""");

        plan.Sql.ShouldNotContain("OR '1'");
        plan.Sql.ShouldNotContain("x'");
        plan.Apply(new GovernedSqlQueryRequest(Stored)).GatewayParameters!["@__gql_tf_0"].ShouldBe("x' OR '1'='1");
    }

    [Fact]
    public void The_count_request_has_no_page_window_and_no_cache_participation()
    {
        var plan = Plan("""{ "totalCount": true }""");
        var baseRequest = new GovernedSqlQueryRequest(Stored, Page: new SqlPageWindow(0, 10, 1), NextToken: "t");

        var count = plan.ApplyCount(baseRequest)!;

        count.Page.ShouldBeNull();
        count.NextToken.ShouldBeNull();
        count.Sql.ShouldStartWith("SELECT COUNT(*)");
        Plan("""{ }""").ApplyCount(baseRequest).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void A_page_token_binds_the_view_values()
    {
        var baseRequest = new GovernedSqlQueryRequest(Stored, new Dictionary<string, object?> { ["ab"] = "2026-01-01" });
        var a = Plan("""{ "filter": { "column": "werk", "op": "eq", "value": "Ehingen" } }""").Apply(baseRequest);
        var b = Plan("""{ "filter": { "column": "werk", "op": "eq", "value": "Nenzing" } }""").Apply(baseRequest);

        a.Sql.ShouldBe(b.Sql);
        a.TokenBoundParameters!["@__gql_tf_0"].ShouldBe("Ehingen");
        b.TokenBoundParameters!["@__gql_tf_0"].ShouldBe("Nenzing");
        a.TokenBoundParameters["ab"].ShouldBe("2026-01-01");
        Autheris.Application.Sql.Paging.WebSqlPageTokenCodec.ComputeParametersHash(a.TokenBoundParameters)
            .ShouldNotBe(Autheris.Application.Sql.Paging.WebSqlPageTokenCodec.ComputeParametersHash(b.TokenBoundParameters));
    }
}
