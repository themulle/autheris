namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Metamorphic testing suite for GraphQL Query Complexity Analysis and Governed Aggregation Cost Models.
/// Validates mathematical invariants: Selection set commutativity, Subset monotonicity,
/// Paging multiplier monotonicity, Fragment expansion equivalence, and CountDistinct dominance.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "Metamorphic")]
[Trait("Category", "FinOps")]
public sealed class GraphQlCostMetamorphicTests
{
    #region Schema Definition for GraphQL Testing

    public sealed class User
    {
        public string Id { get; set; } = "1";
        public string Name { get; set; } = "Alice";
        public string Email { get; set; } = "alice@example.com";
        public List<Order> Orders { get; set; } = [];
    }

    public sealed class Order
    {
        public string Id { get; set; } = "100";
        public string Total { get; set; } = "$99";
        public List<Item> Items { get; set; } = [];
    }

    public sealed class Item
    {
        public string Id { get; set; } = "item-1";
        public string Name { get; set; } = "Widget";
        public decimal Price { get; set; } = 9.99m;
    }

    public sealed class TestQuery
    {
        public List<User> Users => [new()];
        public List<Order> Orders => [new()];
        public List<Item> GetItems(int first = 10) => [new()];
    }

    private static async Task<ISchemaDefinition> CreateSchemaAsync()
    {
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<TestQuery>()
            .BuildRequestExecutorAsync();

        return executor.Schema;
    }

    #endregion

    #region 1. GraphQL Selection Set Commutativity & Monotonicity Invariants

    [Fact]
    public async Task M_GQL_01_FieldOrderCommutativity_ReorderingFields_YieldsIdenticalCost()
    {
        // Invariant: Permuting the order of fields in a selection set must yield the exact same cost.
        var schema = await CreateSchemaAsync();

        var docA = Utf8GraphQLParser.Parse("{ users { id name email } orders { id total } }");
        var docB = Utf8GraphQLParser.Parse("{ orders { total id } users { email name id } }");

        int costA = QueryCostAnalyzerRule.CalculateCost(docA, schema);
        int costB = QueryCostAnalyzerRule.CalculateCost(docB, schema);

        costA.ShouldBeGreaterThan(0);
        costA.ShouldBe(costB);
    }

    [Fact]
    public async Task M_GQL_02_SubAdditivityAndMonotonicity_SubsetOfFields_NeverExceedsSupersetCost()
    {
        // Invariant: A query requesting a strict subset of fields must have Cost(Q_subset) <= Cost(Q_superset).
        var schema = await CreateSchemaAsync();

        var docSubset = Utf8GraphQLParser.Parse("{ users { id } }");
        var docSuperset = Utf8GraphQLParser.Parse("{ users { id name email } }");

        int costSubset = QueryCostAnalyzerRule.CalculateCost(docSubset, schema);
        int costSuperset = QueryCostAnalyzerRule.CalculateCost(docSuperset, schema);

        costSubset.ShouldBeGreaterThan(0);
        costSuperset.ShouldBeGreaterThan(costSubset);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(10, 25)]
    [InlineData(25, 50)]
    public async Task M_GQL_03_PagingMultiplierMonotonicity_HigherFirstArgument_YieldsHigherOrEqualCost(int smallFirst, int largeFirst)
    {
        // Invariant: Increasing the 'first' argument on a paged field must monotonically increase cost.
        var schema = await CreateSchemaAsync();

        var docSmall = Utf8GraphQLParser.Parse($"{{ getItems(first: {smallFirst}) {{ id name }} }}");
        var docLarge = Utf8GraphQLParser.Parse($"{{ getItems(first: {largeFirst}) {{ id name }} }}");

        int costSmall = QueryCostAnalyzerRule.CalculateCost(docSmall, schema);
        int costLarge = QueryCostAnalyzerRule.CalculateCost(docLarge, schema);

        costLarge.ShouldBeGreaterThanOrEqualTo(costSmall);
    }

    [Fact]
    public async Task M_GQL_04_FragmentExpansionEquivalence_InlineVsNamedFragment_YieldsIdenticalCost()
    {
        // Invariant: Encapsulating a field set in a named fragment must evaluate to the exact same cost
        // as specifying those fields directly inline.
        var schema = await CreateSchemaAsync();

        var docDirect = Utf8GraphQLParser.Parse("{ users { id name email } }");
        var docFragment = Utf8GraphQLParser.Parse(@"
            query {
                users {
                    ...UserFields
                }
            }
            fragment UserFields on User {
                id
                name
                email
            }");

        int costDirect = QueryCostAnalyzerRule.CalculateCost(docDirect, schema);
        int costFragment = QueryCostAnalyzerRule.CalculateCost(docFragment, schema);

        costDirect.ShouldBeGreaterThan(0);
        costFragment.ShouldBe(costDirect);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void M_GQL_05_RootFieldCount_AliasAmplification_ScalesLinearly(int count)
    {
        // Invariant: CountMaxRootFields accurately counts aliased root fields linearly.
        var selections = string.Join(" ", Enumerable.Range(1, count).Select(i => $"u{i}: users {{ id }}"));
        var doc = Utf8GraphQLParser.Parse($"{{ {selections} }}");

        int counted = QueryCostAnalyzerRule.CountMaxRootFields(doc, stopAfter: 50);
        counted.ShouldBe(count);
    }

    #endregion

    #region 2. Governed Aggregation Cost Model Invariants

    [Fact]
    public void M_AGG_01_GroupByCommutativity_AnyOrder_YieldsIdenticalCost()
    {
        // Invariant: In Governed Aggregations, reordering the GROUP BY columns must yield identical cost.
        var queryA = new AggregationQuery(
            Table: TableIdentifier.Parse("finance.revenue"),
            GroupBy: ["country", "department", "fiscal_year"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "total_revenue")]);

        var queryB = new AggregationQuery(
            Table: TableIdentifier.Parse("finance.revenue"),
            GroupBy: ["fiscal_year", "country", "department"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "amount", "total_revenue")]);

        double costA = AggregationCostCalculator.CalculateCost(queryA);
        double costB = AggregationCostCalculator.CalculateCost(queryB);

        costA.ShouldBeGreaterThan(0.0);
        costA.ShouldBe(costB);
    }

    [Fact]
    public void M_AGG_02_MonotonicityOnAggregateAddition_AddingProjections_IncreasesCost()
    {
        // Invariant: Adding aggregate projections monotonically increases the query cost.
        var baseQuery = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            GroupBy: ["region"],
            Aggregates: [new AggregateSelection(AggregateFunction.Sum, "sales", "total_sales")]);

        var extendedQuery = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            GroupBy: ["region"],
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Sum, "sales", "total_sales"),
                new AggregateSelection(AggregateFunction.Avg, "margin", "avg_margin")
            ]);

        double baseCost = AggregationCostCalculator.CalculateCost(baseQuery);
        double extendedCost = AggregationCostCalculator.CalculateCost(extendedQuery);

        extendedCost.ShouldBeGreaterThan(baseCost);
        // Specifically, one standard aggregate adds AggregateSelectionWeight (5.0)
        (extendedCost - baseCost).ShouldBe(AggregationCostCalculator.AggregateSelectionWeight);
    }

    [Fact]
    public void M_AGG_03_CountDistinct_Dominates_StandardAggregateWeight()
    {
        // Invariant: CountDistinct incurs a strictly higher computational cost than standard Sum/Avg/Count (15.0 vs 5.0).
        var queryStandard = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            Aggregates: [new AggregateSelection(AggregateFunction.Count, "customer_id", "cnt_customers")]);

        var queryDistinct = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            Aggregates: [new AggregateSelection(AggregateFunction.CountDistinct, "customer_id", "uniq_customers")]);

        double costStandard = AggregationCostCalculator.CalculateCost(queryStandard);
        double costDistinct = AggregationCostCalculator.CalculateCost(queryDistinct);

        costDistinct.ShouldBeGreaterThan(costStandard);
        (costDistinct - costStandard).ShouldBe(AggregationCostCalculator.CountDistinctWeight - AggregationCostCalculator.AggregateSelectionWeight);
    }

    [Fact]
    public void M_AGG_04_BoundsValidation_EnforcesSafetyCeilings_FailClosed()
    {
        // Invariant: Aggregation queries exceeding configured safety bounds are rejected fail-closed.
        var options = new AggregationOptions
        {
            Enabled = true,
            MaxGroupByColumns = 2,
            MaxAggregates = 2
        };

        var excessiveGroupBy = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            GroupBy: ["c1", "c2", "c3"]);

        var excessiveAggs = new AggregationQuery(
            Table: TableIdentifier.Parse("sales.orders"),
            Aggregates:
            [
                new AggregateSelection(AggregateFunction.Sum, "f1", "a1"),
                new AggregateSelection(AggregateFunction.Sum, "f2", "a2"),
                new AggregateSelection(AggregateFunction.Sum, "f3", "a3")
            ]);

        Should.Throw<ArgumentException>(() => AggregationCostCalculator.ValidateBounds(excessiveGroupBy, options));
        Should.Throw<ArgumentException>(() => AggregationCostCalculator.ValidateBounds(excessiveAggs, options));

        // When disabled
        var disabledOptions = new AggregationOptions { Enabled = false };
        var normalQuery = new AggregationQuery(Table: TableIdentifier.Parse("sales.orders"));
        Should.Throw<GatewayForbiddenException>(() => AggregationCostCalculator.ValidateBounds(normalQuery, disabledOptions));
    }

    #endregion
}
