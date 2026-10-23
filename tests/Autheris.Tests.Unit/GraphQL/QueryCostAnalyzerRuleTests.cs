namespace Autheris.Tests.Unit.GraphQL;

using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>#302 GAP-011: <c>first: 0</c> is a bounded, priced query, not an unbounded one.</summary>
public sealed class QueryCostAnalyzerRuleTests
{
    public sealed class Order
    {
        public string Id { get; set; } = "order-1";
    }

    public sealed class Query
    {
        public List<Order> GetOrders(int first = 10) => [new()];
    }

    private static async Task<ISchemaDefinition> SchemaAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer().AddQueryType<Query>();
        var provider = services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
        return (await provider.GetExecutorAsync()).Schema;
    }

    private static int Cost(ISchemaDefinition schema, string query, Dictionary<string, object?>? variables = null) =>
        QueryCostAnalyzerRule.CalculateCost(
            Utf8GraphQLParser.Parse(query), schema, defaultListMultiplier: 10, maxResponseRows: 1000, variableValues: variables);

    [Fact]
    public async Task FirstZero_IsNotUnbounded()
    {
        var schema = await SchemaAsync();

        var zero = Cost(schema, "query { orders(first: 0) { id } }");
        var max = Cost(schema, "query { orders(first: 1000) { id } }");

        zero.ShouldBeLessThan(max);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FirstZero_IsBoundedAndPriced()
    {
        // SEC-PG-07: never free (cost 0) and never the worst case; a variable zero is priced like a literal zero.
        var schema = await SchemaAsync();

        var zero = Cost(schema, "query { orders(first: 0) { id } }");
        var one = Cost(schema, "query { orders(first: 1) { id } }");
        var viaVariable = Cost(schema, "query($n: Int) { orders(first: $n) { id } }", new Dictionary<string, object?> { ["n"] = 0 });

        zero.ShouldBeGreaterThan(0);
        zero.ShouldBe(one);
        viaVariable.ShouldBe(zero);
    }
}
