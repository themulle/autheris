namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class QueryCostFragmentDepthTests
{
    public sealed class Item
    {
        public string Id { get; set; } = "1";
        public string Name { get; set; } = "Item1";
        public string Email { get; set; } = "item@example.com";
        public Item? Child { get; set; }
    }

    public sealed class User
    {
        public string Id { get; set; } = "1";
        public string Name { get; set; } = "Alice";
        public string Email { get; set; } = "alice@example.com";
        public Item? Item { get; set; }
    }

    public sealed class DepthTestQuery
    {
        public User User => new();
        public Item RootItem => new();
    }

    private static async Task<ISchemaDefinition> CreateSchemaAsync()
    {
        var executor = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<DepthTestQuery>()
            .BuildRequestExecutorAsync();

        return executor.Schema;
    }

    [Fact]
    public async Task Cost_25NestedInlineFragments_EqualsUnwrappedCost()
    {
        var schema = await CreateSchemaAsync();
        var unwrappedDoc = Utf8GraphQLParser.Parse("{ user { id name } }");
        var unwrappedCost = QueryCostAnalyzerRule.CalculateCost(unwrappedDoc, schema);

        // Build 25 nested inline fragments: ... on User { ... on User { ... id name } }
        var sb = new StringBuilder("{ user { ");
        for (int i = 0; i < 25; i++)
        {
            sb.Append("... on User { ");
        }
        sb.Append("id name ");
        for (int i = 0; i < 25; i++)
        {
            sb.Append("} ");
        }
        sb.Append("} }");

        var nestedDoc = Utf8GraphQLParser.Parse(sb.ToString());
        var nestedCost = QueryCostAnalyzerRule.CalculateCost(nestedDoc, schema);

        nestedCost.ShouldBe(unwrappedCost);
    }

    [Fact]
    public async Task Cost_NestedFragmentSpreads_EqualsUnwrappedCost()
    {
        var schema = await CreateSchemaAsync();
        var unwrappedDoc = Utf8GraphQLParser.Parse("{ user { id name } }");
        var unwrappedCost = QueryCostAnalyzerRule.CalculateCost(unwrappedDoc, schema);

        var query = @"
            query { user { ...F1 } }
            fragment F1 on User { ...F2 }
            fragment F2 on User { ...F3 }
            fragment F3 on User { id name }";

        var fragmentDoc = Utf8GraphQLParser.Parse(query);
        var fragmentCost = QueryCostAnalyzerRule.CalculateCost(fragmentDoc, schema);

        fragmentCost.ShouldBe(unwrappedCost);
    }

    [Fact]
    public async Task Cost_FieldDepthExceeded_ExceedsBudget()
    {
        var schema = await CreateSchemaAsync();
        // Depth 2: query { rootItem { child { child { id } } } } has field depth 4
        var query = "{ rootItem { child { child { id } } } }";
        var doc = Utf8GraphQLParser.Parse(query);

        var rule = new QueryCostAnalyzerRule(
            maxAllowedCost: 100,
            maxExecutionDepth: 2);

        var cost = rule.ComputeCost(doc, schema);
        cost.ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Cost_FragmentFirstSpreadDeep_ReusedAtRoot_NotZero()
    {
        var schema = await CreateSchemaAsync();
        // Fragment F is evaluated at a deep depth first, then reused at root
        var query = @"
            query {
                rootItem { child { child { child { ...F } } } }
                user { ...F }
            }
            fragment F on Item { id name }";

        var doc = Utf8GraphQLParser.Parse(query);
        var rule = new QueryCostAnalyzerRule(
            maxAllowedCost: 500,
            maxExecutionDepth: 3);

        var (totalCost, details) = rule.ComputeCostDetails(doc, schema);

        // Root field 'user' using fragment F must not be 0
        details.ShouldContainKey("user");
        details["user"].ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task MaskingCost_NestedFragments_NotZero()
    {
        var schema = await CreateSchemaAsync();
        var query = @"
            query {
                user {
                    ... on User {
                        ... on User {
                            email
                        }
                    }
                }
            }";

        var doc = Utf8GraphQLParser.Parse(query);
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500);

        var cost = rule.ComputeCost(doc, schema);
        // Cost should include scalar cost + masking cost for email
        cost.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void CostAndQuotaMiddleware_UsesConfiguredDepth()
    {
        var options = new GraphQLOptions
        {
            MaxAllowedExecutionDepth = 7
        };

        var resolvedDepth = QueryCostAnalyzerRule.ResolveMaxExecutionDepth(options);
        resolvedDepth.ShouldBe(7);

        var defaultOptions = new GraphQLOptions();
        var defaultDepth = QueryCostAnalyzerRule.ResolveMaxExecutionDepth(defaultOptions);
        defaultDepth.ShouldBe(6); // default Range(1, 25) initialized to 6 in GraphQlOptions

        var nullResolved = QueryCostAnalyzerRule.ResolveMaxExecutionDepth((GraphQLOptions?)null);
        nullResolved.ShouldBe(25);
    }
}
