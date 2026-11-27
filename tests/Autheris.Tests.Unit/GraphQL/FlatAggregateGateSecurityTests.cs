using Autheris.Api.Middleware;
using Autheris.Application.Analytics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.AggregateCostAnalysisTests;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The pre-execution gates (cost rule, quota table mapping, enumeration shield, MCP map) recognise the flat <c>_groups</c> root and fail closed.</summary>
[Trait("Category", "Security")]
public sealed class FlatAggregateGateSecurityTests
{
    private static readonly CatalogTableType Sales = FlatAggregateTestSupport.SalesTable();

    private static string Flat(string selection) => $"{{ sales_groups {{ {selection} }} }}";

    [Fact]
    public async Task Cost_UnknownTableFeature_WorstCase()
    {
        var known = await CostAsync(Flat("region count"));
        var unknown = await CostAsync("{ plain_groups { region count } }");

        var worst = AggregationOptions.DefaultAggregateFieldCostWeight
                    + (AggregationOptions.DefaultMaxGroupByColumns * AggregationCostCalculator.GroupByColumnWeight)
                    + (AggregationOptions.DefaultMaxAggregates * AggregationCostCalculator.CountDistinctWeight);
        unknown.ShouldBe((int)Math.Ceiling(worst));
        unknown.ShouldBeGreaterThan(known);
    }

    [Fact]
    public void Quota_GroupsRoot_Aliased_ResolvesByFieldName_Denies_WhenUnresolvable()
    {
        var meta = TypedAggregateTestHost.DefaultTable();
        TableMetadata? Lookup(string name) => name == "sales_dbo_orders" ? meta : null;
        var aliased = Utf8GraphQLParser.Parse("{ werte: sales_dbo_orders_groups { count } other: nope_groups { count } }");

        var names = CostAndQuotaMiddleware.CollectRootFieldNames(aliased);

        names.ShouldBe(["sales_dbo_orders_groups", "nope_groups"]);
        AggregateRootResolution.Resolve(names[0], Lookup, out var baseName).ShouldBeSameAs(meta);
        baseName.ShouldBe("sales_dbo_orders");
        AggregateRootResolution.Resolve(names[1], Lookup, out _).ShouldBeNull();
        AggregateRootResolution.Resolve("werte", Lookup, out _).ShouldBeNull();
    }

    private static (bool Ok, HashSet<string> Required, HashSet<string> Clear) Shield(string query)
    {
        var document = Utf8GraphQLParser.Parse(query);
        var root = (FieldNode)document.Definitions.OfType<OperationDefinitionNode>().Single().SelectionSet.Selections[0];
        var fragments = document.Definitions.OfType<FragmentDefinitionNode>().ToDictionary(f => f.Name.Value, StringComparer.Ordinal);
        var tables = new Dictionary<TableIdentifier, CatalogTableType>();
        var required = new Dictionary<TableIdentifier, HashSet<string>>();
        var clear = new Dictionary<TableIdentifier, HashSet<string>>();

        var ok = GraphQlEnumerationShieldMiddleware.TryCollectRootCatalogField(root, Sales, null!, fragments, tables, required, clear);

        return (ok, required.GetValueOrDefault(Sales.Identifier) ?? [], clear.GetValueOrDefault(Sales.Identifier) ?? []);
    }

    [Fact]
    public void Shield_UnknownRowField_Denied()
    {
        Shield($"{{ {FlatAggregateTestSupport.Root} {{ region secret }} }}").Ok.ShouldBeFalse();
        Shield($"{{ {FlatAggregateTestSupport.Root} {{ region ... on x {{ secret: ssn }} }} }}").Ok.ShouldBeFalse();
    }

    [Fact]
    public void Shield_CountMergedOtherTypename_Allowed_AddNoColumns()
    {
        var (ok, required, clear) = Shield($"{{ {FlatAggregateTestSupport.Root} {{ count _isMergedOther __typename amount_sum n: qty_count_distinct }} }}");

        ok.ShouldBeTrue();
        required.ShouldBeEmpty();
        clear.ShouldBeEmpty();
    }

    [Fact]
    public async Task MaxFieldsPerOperation_MixedNestedFlatMetrics_SharedPool()
    {
        const string Agg = "{ fn: COUNT, alias: \"n\" }";
        var query = $"{{ a: sales_aggregate(aggregates: [{Agg}]) {{ count }} b: sales_groups {{ count }} c: queryMetrics(metrics: [\"m\"]) {{ metrics }} }}";

        ErrorCodes(await RunWithCapAsync(query, 2)).ShouldContain("QUERY_TOO_COMPLEX");
        ErrorCodes(await RunWithCapAsync(query, 3)).ShouldNotContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task Cost_ManyResponseKeysOnOneRowField_AliasAmplification_Rejected()
    {
        var executor = await Builder()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: 100_000))
            .BuildRequestExecutorAsync();
        var cap = 2 * (AggregationOptions.DefaultMaxGroupByColumns + AggregationOptions.DefaultMaxAggregates);
        string Keys(int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => $"k{i}: count"));

        ErrorCodes(await executor.ExecuteAsync(Flat(Keys(cap + 1)))).ShouldContain("QUERY_TOO_COMPLEX");
        ErrorCodes(await executor.ExecuteAsync(Flat(Keys(cap)))).ShouldNotContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task Cost_FlatEqualsNested_SameGroupsAggregates()
    {
        var nested = await CostAsync("{ sales_aggregate(groupBy: [region], aggregates: [{ fn: SUM, column: qty, alias: \"a\" }, { fn: COUNT, alias: \"c\" }]) { count } }");
        var flat = await CostAsync(Flat("region qty_sum count"));

        flat.ShouldBe(nested);
    }
}
