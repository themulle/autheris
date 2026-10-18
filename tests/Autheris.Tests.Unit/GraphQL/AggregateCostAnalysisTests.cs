using Autheris.Application.Analytics;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-7 / T-5.11 / T-S-16 / SEC-112-19: aggregate cost model and the per-operation field cap.</summary>
public sealed class AggregateCostAnalysisTests
{
    private const string Sdl = """
        type Query {
          sales_aggregate(groupBy: [sales_column!], aggregates: [sales_aggregate_input!]!, where: String, limit: Int = 100): sales_aggregate_result!
          hr_aggregate(groupBy: [sales_column!], aggregates: [sales_aggregate_input!]!, limit: Int = 100): sales_aggregate_result!
          queryMetrics(metrics: [String!]!, groupBy: [String!], limit: Int): MetricResult!
          sales(first: Int = 10): [Sale!]!
          plain_aggregate: Sale
        }
        enum sales_column { region qty amount flag }
        enum Fn { COUNT COUNT_DISTINCT SUM }
        input sales_aggregate_input { fn: Fn! column: sales_column alias: String! }
        type sales_aggregate_result { groups: [Group!]! count: Long! }
        type Group { key: Key! rowCount: Long! aggregates: [Value!]! }
        type Key { region: String qty: Int }
        type Value { alias: String! kind: String! intValue: Long }
        type MetricResult { metrics: [String!]! }
        type Sale { id: Int }
        scalar Long
        """;

    private static IRequestExecutorBuilder Builder()
    {
        var builder = new ServiceCollection().AddGraphQLServer().AddDocumentFromString(Sdl);
        foreach (var type in Utf8GraphQLParser.Parse(Sdl).Definitions.OfType<ObjectTypeDefinitionNode>())
        {
            foreach (var field in type.Fields)
            {
                builder = builder.AddResolver(type.Name.Value, field.Name.Value, _ => (object?)null);
            }
        }

        return builder;
    }

    private static async Task<ISchemaDefinition> SchemaAsync() => await Builder().BuildSchemaAsync();

    private static async Task<int> CostAsync(string query, IReadOnlyDictionary<string, object?>? variables = null)
    {
        var schema = await SchemaAsync();
        return QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse(query), schema, variableValues: variables);
    }

    private static string Q(string groupBy, string aggregates, string selection = "groups { rowCount }") =>
        $"{{ sales_aggregate(groupBy: [{groupBy}], aggregates: [{aggregates}]) {{ {selection} }} }}";

    private const string One = "{ fn: SUM, column: qty, alias: \"a\" }";
    private const string Distinct = "{ fn: COUNT_DISTINCT, column: region, alias: \"d\" }";

    [Fact]
    public async Task Cost_ModelsGroupByAndAggregates_WithTheCalculatorWeights()
    {
        var cost = await CostAsync(Q("region, qty", $"{One}, {Distinct}"));

        cost.ShouldBe((int)(AggregationOptions.DefaultAggregateFieldCostWeight + 2 * AggregationCostCalculator.GroupByColumnWeight
            + AggregationCostCalculator.AggregateSelectionWeight + AggregationCostCalculator.CountDistinctWeight));
    }

    [Fact]
    public async Task Cost_GrowsWithGroupByAndAggregateCount()
    {
        var small = await CostAsync(Q("region", One));
        var moreGroups = await CostAsync(Q("region, qty", One));
        var moreAggregates = await CostAsync(Q("region", $"{One}, {{ fn: COUNT, alias: \"c\" }}"));

        moreGroups.ShouldBeGreaterThan(small);
        moreAggregates.ShouldBeGreaterThan(small);
    }

    [Fact]
    public async Task Cost_CountDistinct_CostsMoreThanOtherFunctions()
    {
        var sum = await CostAsync(Q("region", One));
        var distinct = await CostAsync(Q("region", Distinct));

        distinct.ShouldBeGreaterThan(sum);
    }

    [Fact]
    public async Task Cost_SelectingMoreSubfields_DoesNotMultiplyTheCost()
    {
        var narrow = await CostAsync(Q("region", One, "count"));
        var wide = await CostAsync(Q("region", One, "groups { key { region qty } rowCount aggregates { alias kind intValue } } count"));

        wide.ShouldBe(narrow);
    }

    [Fact]
    public async Task Cost_DoesNotUseTheRowLimitMultiplier()
    {
        var withLimit = await CostAsync("{ sales_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }], limit: 5000) { count } }");
        var withoutLimit = await CostAsync("{ sales_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }");

        withLimit.ShouldBe(withoutLimit);
        withLimit.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task Cost_UsesRuntimeVariableValues()
    {
        const string query = "query($g: [sales_column!], $a: [sales_aggregate_input!]!) { sales_aggregate(groupBy: $g, aggregates: $a) { count } }";
        var small = Utf8GraphQLParser.Syntax.ParseValueLiteral("[region]");
        var big = Utf8GraphQLParser.Syntax.ParseValueLiteral("[region, qty, amount, flag]");
        var aggs = Utf8GraphQLParser.Syntax.ParseValueLiteral("[{ fn: SUM, column: qty, alias: \"a\" }]");

        var low = await CostAsync(query, new Dictionary<string, object?> { ["g"] = small, ["a"] = aggs });
        var high = await CostAsync(query, new Dictionary<string, object?> { ["g"] = big, ["a"] = aggs });

        high.ShouldBe(low + 3 * (int)AggregationCostCalculator.GroupByColumnWeight);
    }

    [Fact]
    public async Task Cost_UnresolvedVariables_AssumeTheWorstCase()
    {
        const string query = "query($g: [sales_column!], $a: [sales_aggregate_input!]!) { sales_aggregate(groupBy: $g, aggregates: $a) { count } }";
        var opts = new AggregationOptions();

        var cost = await CostAsync(query);

        cost.ShouldBe((int)(opts.AggregateFieldCostWeight + opts.MaxGroupByColumns * AggregationCostCalculator.GroupByColumnWeight
            + opts.MaxAggregates * AggregationCostCalculator.CountDistinctWeight));
    }

    [Fact]
    public async Task Cost_VariableDefaultValues_AreUsedWhenNoRuntimeValueExists()
    {
        const string query = "query($g: [sales_column!] = [region]) { sales_aggregate(groupBy: $g, aggregates: [{ fn: SUM, column: qty, alias: \"a\" }]) { count } }";

        var cost = await CostAsync(query);

        cost.ShouldBe(await CostAsync(Q("region", One, "count")));
    }

    [Fact]
    public async Task Cost_OverTheBudget_IsRejectedAsQueryTooComplex()
    {
        var schema = await SchemaAsync();
        var executor = await Builder()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: 100))
            .BuildRequestExecutorAsync();

        var manyAggregates = string.Join(", ", Enumerable.Range(0, 8).Select(i => $"{{ fn: COUNT_DISTINCT, column: region, alias: \"d{i}\" }}"));
        var result = await executor.ExecuteAsync(Q("region, qty, amount, flag", manyAggregates));

        ErrorCodes(result).ShouldContain("QUERY_TOO_COMPLEX");
        schema.ShouldNotBeNull();
    }

    private static List<string?> ErrorCodes(IExecutionResult result) => Errors(result).Select(e => e.Code).ToList();

    private static List<(string? Code, string Message)> Errors(IExecutionResult result)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(result.ToJson());
        var list = new List<(string?, string)>();
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            foreach (var e in errors.EnumerateArray())
            {
                string? code = e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) ? c.GetString() : null;
                list.Add((code, e.GetProperty("message").GetString() ?? string.Empty));
            }
        }

        return list;
    }

    private static async Task<IExecutionResult> RunWithCapAsync(string query, int cap)
    {
        var executor = await Builder()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: 100_000, maxAggregateFieldsPerOperation: cap))
            .BuildRequestExecutorAsync();
        return await executor.ExecuteAsync(query);
    }

    private const string Field = "{ fn: COUNT, alias: \"n\" }";

    [Trait("Category", "Security")]
    [Fact]
    public async Task MaxFieldsPerOperation_AliasedFields_Rejected_BeforeExecution()
    {
        var query = $"{{ a: sales_aggregate(aggregates: [{Field}]) {{ count }} b: sales_aggregate(aggregates: [{Field}]) {{ count }} " +
                    $"c: sales_aggregate(aggregates: [{Field}]) {{ count }} d: sales_aggregate(aggregates: [{Field}]) {{ count }} }}";

        var result = await RunWithCapAsync(query, 3);

        var error = Errors(result).Single(e => e.Code == "QUERY_TOO_COMPLEX");
        error.Message.ShouldNotContain("sales");
        error.Message.ShouldNotContain("aggregate_result");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task MaxFieldsPerOperation_AtTheLimit_IsAllowed()
    {
        var query = $"{{ a: sales_aggregate(aggregates: [{Field}]) {{ count }} b: hr_aggregate(aggregates: [{Field}]) {{ count }} " +
                    "c: queryMetrics(metrics: [\"m\"]) { metrics } }";

        var result = await RunWithCapAsync(query, 3);

        ErrorCodes(result).ShouldNotContain("QUERY_TOO_COMPLEX");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task MaxFieldsPerOperation_SumsAggregateAndMetricFields_AcrossFragments()
    {
        var query = $"query {{ sales_aggregate(aggregates: [{Field}]) {{ count }} ...F ... on Query {{ m2: queryMetrics(metrics: [\"x\"]) {{ metrics }} }} }} " +
                    $"fragment F on Query {{ m1: queryMetrics(metrics: [\"x\"]) {{ metrics }} }}";

        var result = await RunWithCapAsync(query, 2);

        ErrorCodes(result).ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task MaxFieldsPerOperation_OtherRootFields_AreNotCounted()
    {
        var schema = await SchemaAsync();
        var doc = Utf8GraphQLParser.Parse("{ a: sales(first: 1) { id } b: sales(first: 1) { id } c: plain_aggregate { id } d: sales_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }");

        QueryCostAnalyzerRule.CountAggregateRootFields(doc, schema).ShouldBe(1);
    }
}
