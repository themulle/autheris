namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

public sealed class QueryCostAnalyzerDetailsTests
{
    public sealed class User
    {
        public string Id { get; set; } = "1";
        public string Name { get; set; } = "Alice";
    }

    public sealed class Order
    {
        public string Id { get; set; } = "100";
        public string Total { get; set; } = "$99";
    }

    public sealed class Item
    {
        public string Id { get; set; } = "item-1";
        public string Name { get; set; } = "Widget";
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

    [Fact]
    public async Task QueryWithMultipleRootFields_ReturnsBreakdownForEachRootField()
    {
        var schema = await CreateSchemaAsync();
        var doc = Utf8GraphQLParser.Parse("{ users { id name } orders { id total } }");

        var (totalCost, details) = QueryCostAnalyzerRule.CalculateCostDetails(doc, schema);

        details.ShouldNotBeNull();
        details.ContainsKey("users").ShouldBeTrue();
        details.ContainsKey("orders").ShouldBeTrue();
        details.Count.ShouldBe(2);

        var isolatedUsers = QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse("{ users { id name } }"), schema);
        var isolatedOrders = QueryCostAnalyzerRule.CalculateCost(Utf8GraphQLParser.Parse("{ orders { id total } }"), schema);

        details["users"].ShouldBe(isolatedUsers);
        details["orders"].ShouldBe(isolatedOrders);
        totalCost.ShouldBe(isolatedUsers + isolatedOrders);
    }

    [Fact]
    public async Task QueryWithAliasedRootFields_UsesAliasKey()
    {
        var schema = await CreateSchemaAsync();
        var doc = Utf8GraphQLParser.Parse("{ u1: users { id } u2: users { name } }");

        var (totalCost, details) = QueryCostAnalyzerRule.CalculateCostDetails(doc, schema);

        details.ContainsKey("u1").ShouldBeTrue();
        details.ContainsKey("u2").ShouldBeTrue();
        details.ContainsKey("users").ShouldBeFalse();
        totalCost.ShouldBe(details["u1"] + details["u2"]);
    }

    [Fact]
    public async Task QueryWithPagination_ScalesChildCostWithLimit()
    {
        var schema = await CreateSchemaAsync();
        var docSmall = Utf8GraphQLParser.Parse("{ items(first: 5) { id name } }");
        var docLarge = Utf8GraphQLParser.Parse("{ items(first: 50) { id name } }");

        var (costSmall, detailsSmall) = QueryCostAnalyzerRule.CalculateCostDetails(docSmall, schema);
        var (costLarge, detailsLarge) = QueryCostAnalyzerRule.CalculateCostDetails(docLarge, schema);

        detailsSmall["items"].ShouldBe(costSmall);
        detailsLarge["items"].ShouldBe(costLarge);
        costLarge.ShouldBeGreaterThan(costSmall);
    }

    [Fact]
    public async Task QueryWithVariables_AltersCostCorrectly()
    {
        var schema = await CreateSchemaAsync();
        var doc = Utf8GraphQLParser.Parse("query Q($limit: Int) { items(first: $limit) { id name } }");

        var varsSmall = new Dictionary<string, object?> { ["limit"] = 5 };
        var varsLarge = new Dictionary<string, object?> { ["limit"] = 100 };

        var (costSmall, detailsSmall) = QueryCostAnalyzerRule.CalculateCostDetails(doc, schema, variableValues: varsSmall);
        var (costLarge, detailsLarge) = QueryCostAnalyzerRule.CalculateCostDetails(doc, schema, variableValues: varsLarge);

        detailsSmall["items"].ShouldBe(costSmall);
        detailsLarge["items"].ShouldBe(costLarge);
        costLarge.ShouldBeGreaterThan(costSmall);
    }

    [Fact]
    public async Task MathematicalIdentity_TotalCostEqualsSaturatingSumOfRootCosts()
    {
        var schema = await CreateSchemaAsync();
        var doc = Utf8GraphQLParser.Parse("{ u: users { id } o: orders { id } i: items(first: 20) { id } }");

        var (totalCost, details) = QueryCostAnalyzerRule.CalculateCostDetails(doc, schema);

        var sumOfDetails = details.Values.Sum();
        totalCost.ShouldBe(Math.Max(1, sumOfDetails));
    }

    [Fact]
    public async Task RootLevelFragments_HandledGracefullyWithoutException()
    {
        var schema = await CreateSchemaAsync();
        var inlineDoc = Utf8GraphQLParser.Parse("query { ... on TestQuery { users { id } } }");
        var spreadDoc = Utf8GraphQLParser.Parse("query { ...UserFrag } fragment UserFrag on TestQuery { users { id } }");

        var (costInline, detailsInline) = QueryCostAnalyzerRule.CalculateCostDetails(inlineDoc, schema);
        costInline.ShouldBeGreaterThan(0);
        detailsInline.Count.ShouldBeGreaterThan(0);

        var (costSpread, detailsSpread) = QueryCostAnalyzerRule.CalculateCostDetails(spreadDoc, schema);
        costSpread.ShouldBeGreaterThan(0);
        detailsSpread.Count.ShouldBeGreaterThan(0);
    }
}
