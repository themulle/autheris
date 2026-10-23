using System.Text.Json;
using Autheris.Tests.Parity.Channels;

namespace Autheris.Tests.Parity.Boundary;

/// <summary>Malformed and unusual requests per channel (invariant sweep: no 5xx, no leak, stable code). A few carry an oracle.</summary>
public static class BoundaryRaw
{
    private sealed record Raw(string Id, RenderedRequest Request, Expectation Expected, IReadOnlyList<int>? Ids = null);

    private const string SqlPath = "/api/v1/sql";
    private const string GqlPath = "/graphql";
    private const string RestBase = "/api/v1/data/default/main/parity_sales";
    private const string IdsSelect = "SELECT id FROM parity_sales";

    private static RenderedRequest Sql(string sql, object? parameters = null) =>
        new("POST", SqlPath, JsonSerializer.Serialize(new { sql, parameters }), SubmittedSql: sql);

    private static RenderedRequest Gql(string query) => new("POST", GqlPath, JsonSerializer.Serialize(new { query }));

    private static RenderedRequest Rest(string query) => new("GET", RestBase + query);

    private static string Nested(int depth) => "SELECT " + new string('(', depth) + "1" + new string(')', depth) + " AS x";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Raw>> Table = new Dictionary<string, IReadOnlyList<Raw>>
    {
        ["WebSql"] =
        [
            new("empty_body", new("POST", SqlPath, string.Empty), Expectation.Rejected),
            new("invalid_json", new("POST", SqlPath, "{"), Expectation.Rejected),
            new("json_array", new("POST", SqlPath, "[]"), Expectation.Rejected),
            new("json_null", new("POST", SqlPath, "null"), Expectation.Rejected),
            new("wrong_content_type", new("POST", SqlPath, "{\"sql\":\"SELECT 1\"}", "text/plain"), Expectation.Rejected),
            new("sql_number", new("POST", SqlPath, "{\"sql\":123}"), Expectation.Rejected),
            new("sql_null", new("POST", SqlPath, "{\"sql\":null}"), Expectation.Rejected),
            new("sql_empty", Sql(string.Empty), Expectation.Rejected),
            new("sql_whitespace", Sql("   \n\t "), Expectation.Rejected),
            new("sql_comment_only", Sql("-- nothing"), Expectation.Rejected),
            new("params_not_object", new("POST", SqlPath, "{\"sql\":\"SELECT 1\",\"parameters\":5}"), Expectation.Rejected),
            new("multi_statement", Sql("SELECT 1; SELECT 2"), Expectation.Rejected),
            new("nested_300", Sql(Nested(300)), Expectation.Either),
            new("long_literal_200k", Sql("SELECT '" + new string('a', 200_000) + "' AS x"), Expectation.Either),
            new("date_literal", Sql($"{IdsSelect} WHERE sale_date = DATE '2024-02-29' ORDER BY id"), Expectation.Ok, [5]),
            new("cast_date", Sql($"{IdsSelect} WHERE sale_date = CAST('2024-02-29' AS DATE) ORDER BY id"), Expectation.Ok, [5]),
            new("unknown_column", Sql($"{IdsSelect} WHERE nope = 1"), Expectation.Rejected),
            new("unknown_table", Sql("SELECT id FROM nope_table"), Expectation.Rejected),
            new("dml_delete", Sql("DELETE FROM parity_sales"), Expectation.Rejected),
            new("dml_drop", Sql("DROP TABLE parity_sales"), Expectation.Rejected),
            new("in_empty_list", Sql($"{IdsSelect} WHERE region IN ()"), Expectation.Rejected),
            new("missing_parameter", Sql($"{IdsSelect} WHERE qty = @missing"), Expectation.Rejected),
            new("param_object", Sql($"{IdsSelect} WHERE qty = @p", new { p = new { a = 1 } }), Expectation.Rejected),
            new("param_array", Sql($"{IdsSelect} WHERE qty = @p", new { p = new[] { 1, 2 } }), Expectation.Rejected),
        ],
        ["GraphQl"] =
        [
            new("empty_body", new("POST", GqlPath, string.Empty), Expectation.Rejected),
            new("invalid_json", new("POST", GqlPath, "{"), Expectation.Rejected),
            new("json_array", new("POST", GqlPath, "[]"), Expectation.Either),
            new("query_null", new("POST", GqlPath, "{\"query\":null}"), Expectation.Rejected),
            new("query_empty", Gql(string.Empty), Expectation.Rejected),
            new("query_syntax_error", Gql("{"), Expectation.Rejected),
            new("query_unknown_field", Gql("{ nope }"), Expectation.Rejected),
            new("query_unknown_argument", Gql("{ default_main_parity_sales(nope: 1) { id } }"), Expectation.Rejected),
            new("query_unterminated_string", Gql("{ default_main_parity_sales(where: {region: {eq: \"x}}) { id } }"), Expectation.Rejected),
            new("variables_not_object", new("POST", GqlPath, "{\"query\":\"{ __typename }\",\"variables\":5}"), Expectation.Rejected),
            new("operation_name_unknown", new("POST", GqlPath, "{\"query\":\"query A { __typename }\",\"operationName\":\"B\"}"), Expectation.Rejected),
            new("mutation_unknown", Gql("mutation { nope }"), Expectation.Rejected),
            new("selection_depth_200", Gql(string.Concat(Enumerable.Repeat("{ a ", 200)) + string.Concat(Enumerable.Repeat("}", 200))), Expectation.Rejected),
            new("alias_flood_200", Gql("{ " + string.Concat(Enumerable.Range(0, 200).Select(i => $"a{i}: __typename ")) + "}"), Expectation.Either),
            new("typename_only", Gql("{ __typename }"), Expectation.Ok),
        ],
        ["Rest"] =
        [
            new("filter_empty", Rest("?filter="), Expectation.Either),
            new("filter_open_parens", Rest("?filter=((((("), Expectation.Rejected),
            new("filter_missing_operand", Rest("?filter=region eq"), Expectation.Rejected),
            new("filter_literal_comparison", Rest("?filter=1 eq 1"), Expectation.Either),
            new("filter_trailing_or", Rest("?filter=region eq 'a' or"), Expectation.Rejected),
            new("filter_unterminated_quote", Rest("?filter=region eq 'a"), Expectation.Rejected),
            new("filter_unknown_column", Rest("?filter=nope eq 1"), Expectation.Rejected),
            new("filter_unknown_operator", Rest("?filter=region sideways 'a'"), Expectation.Rejected),
            new("select_empty", Rest("?select="), Expectation.Either),
            new("select_commas", Rest("?select=,,,"), Expectation.Either),
            new("select_unknown_column", Rest("?select=nope"), Expectation.Rejected),
            new("orderby_empty", Rest("?orderBy="), Expectation.Either),
            new("orderby_bad_direction", Rest("?orderBy=id sideways"), Expectation.Rejected),
            new("orderby_trailing_comma", Rest("?orderBy=id desc,"), Expectation.Either),
            new("limit_twice", Rest("?limit=1&limit=2"), Expectation.Either),
            new("limit_empty", Rest("?limit="), Expectation.Either),
            new("limit_fraction", Rest("?limit=1.5"), Expectation.Rejected),
            new("limit_exponent", Rest("?limit=1e3"), Expectation.Rejected),
            new("limit_hex", Rest("?limit=0x10"), Expectation.Rejected),
            new("offset_text", Rest("?offset=abc"), Expectation.Rejected),
            new("unknown_parameter", Rest("?unknown=1"), Expectation.Either),
            new("encoded_nul", Rest("?filter=region%20eq%20%27%00%27"), Expectation.Either),
            new("unknown_table", new("GET", "/api/v1/data/default/main/nope_table"), Expectation.Rejected),
            new("unknown_domain", new("GET", "/api/v1/data/nope/main/parity_sales"), Expectation.Rejected),
            new("trailing_slash", new("GET", RestBase + "/"), Expectation.Either),
            new("by_id_missing", new("GET", RestBase + "/999999"), Expectation.Either),
            new("by_id_quote", new("GET", RestBase + "/%27%20OR%20%271%27%3D%271"), Expectation.Either),
        ],
    };

    public static IReadOnlyList<string> Ids(string channel) => Table[channel].Select(r => r.Id).ToList();

    public static RenderedRequest Request(string channel, string id) => Table[channel].Single(r => r.Id == id).Request;

    public static BoundaryExpectation Expected(string channel, string id)
    {
        var raw = Table[channel].Single(r => r.Id == id);
        return new BoundaryExpectation(raw.Expected, raw.Ids, true);
    }
}
