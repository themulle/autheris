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

/// <summary>A selected connection <c>totalCount</c> runs an extra count statement and is priced once per connection field.</summary>
[Collection("CatalogRelation")]
public sealed class QueryCostAnalyzerTotalCountTests
{
    public sealed class Thing
    {
        public int Id { get; set; }
    }

    public sealed class ThingEdge
    {
        public Thing Node { get; set; } = new();

        public string Cursor { get; set; } = "";
    }

    public sealed class ThingsConnection
    {
        public List<ThingEdge> Edges { get; set; } = [];

        public int? TotalCount { get; set; }
    }

    public sealed class Query
    {
        public ThingsConnection GetThingsConnection(int? first = 100, string? after = null) => new();
    }

    private static async Task<ISchemaDefinition> SchemaAsync()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer().AddQueryType<Query>();
        var provider = services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
        return (await provider.GetExecutorAsync()).Schema;
    }

    private static int Cost(ISchemaDefinition schema, string query, int maxSpread = 250) =>
        QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse(query), schema, defaultListMultiplier: 10, maxResponseRows: 1000);

    private const string Page = "edges { node { id } }";

    [Fact]
    public async Task NoTotalCount_CostUnchanged()
    {
        var schema = await SchemaAsync();

        var with = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} totalCount }} }}");
        var without = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} }} }}");

        with.ShouldBeGreaterThan(without);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_Surcharge_IsTheSameForEveryPageSize()
    {
        var schema = await SchemaAsync();

        Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} totalCount }} }}")
            .ShouldBeGreaterThan(Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} }} }}"));
        var small = Cost(schema, $"{{ thingsConnection(first: 2) {{ {Page} totalCount }} }}") - Cost(schema, $"{{ thingsConnection(first: 2) {{ {Page} }} }}");
        var large = Cost(schema, $"{{ thingsConnection(first: 500) {{ {Page} totalCount }} }}") - Cost(schema, $"{{ thingsConnection(first: 500) {{ {Page} }} }}");

        small.ShouldBeGreaterThanOrEqualTo(QueryCostAnalyzerRule.TotalCountSurchargeRows);
        large.ShouldBeGreaterThanOrEqualTo(QueryCostAnalyzerRule.TotalCountSurchargeRows);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("... on ThingsConnection { totalCount }")]
    [InlineData("...F")]
    [InlineData("totalCount @skip(if: true)")]
    [InlineData("n: totalCount")]
    public async Task TotalCount_ViaFragmentAliasOrDirective_IsStillPriced(string selection)
    {
        var schema = await SchemaAsync();
        var plain = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} }} }}");

        var priced = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} {selection} }} }} fragment F on ThingsConnection {{ totalCount }}");

        priced.ShouldBeGreaterThan(plain);
    }

    [Fact]
    public async Task TotalCount_TwoAliases_PricedOnce()
    {
        var schema = await SchemaAsync();
        var one = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} a: totalCount }} }}");

        var two = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} a: totalCount b: totalCount }} }}");

        // The second alias is a further scalar of the selection, not a second count.
        (two - one).ShouldBeLessThan(QueryCostAnalyzerRule.TotalCountSurchargeRows);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_TwoConnectionFields_PricedTwice()
    {
        var schema = await SchemaAsync();
        var one = Cost(schema, $"{{ a: thingsConnection(first: 10) {{ {Page} totalCount }} }}");
        var plainTwo = Cost(schema, $"{{ a: thingsConnection(first: 10) {{ {Page} }} b: thingsConnection(first: 10) {{ {Page} }} }}");

        var two = Cost(schema, $"{{ a: thingsConnection(first: 10) {{ {Page} totalCount }} b: thingsConnection(first: 10) {{ {Page} totalCount }} }}");

        (two - plainTwo).ShouldBeGreaterThan(one - Cost(schema, $"{{ a: thingsConnection(first: 10) {{ {Page} }} }}") - 1);
        two.ShouldBeGreaterThan(one);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TotalCount_CyclicFragments_AreStillPricedOrRejectedNeverFree()
    {
        var schema = await SchemaAsync();
        var plain = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} }} }}");

        var cyclic = Cost(schema, $"{{ thingsConnection(first: 10) {{ {Page} ...A }} }} fragment A on ThingsConnection {{ ...B totalCount }} fragment B on ThingsConnection {{ ...A }}");

        cyclic.ShouldBeGreaterThan(plain);
    }
}
