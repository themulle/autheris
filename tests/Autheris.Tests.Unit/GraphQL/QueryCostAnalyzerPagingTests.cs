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

/// <summary>#318 (SEC-RG-05) and #330: how the cost rule prices a variable, null or out-of-range page size on a catalog-shaped field.</summary>
[Collection("CatalogRelation")]
public sealed class QueryCostAnalyzerPagingTests
{
    public sealed class Row
    {
        public int Id { get; set; }

        public List<Row> GetChildren(string? where = null, string? orderBy = null, int? first = 100, int? offset = 0) => [];
    }

    public sealed class Query
    {
        // The catalog shape: where, orderBy, first and offset.
        public List<Row> GetRows(string? where = null, string? orderBy = null, int? first = 100, int? offset = 0) => [];

        // Another field with a page size: no range check of its own at this layer, so it keeps the conservative pricing.
        public List<Row> GetPlain(int first = 10) => [];
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
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_ExplicitNullFirst_IgnoresDeclaredDefault_PricedAsTheDefaultPage()
    {
        var schema = await SchemaAsync();
        const string query = "query($f: Int = 1) { rows(first: $f) { id } }";

        var nullFirst = Cost(schema, query, new Dictionary<string, object?> { ["f"] = null });
        var declaredDefault = Cost(schema, query);
        var defaultPage = Cost(schema, "{ rows(first: 100) { id } }");

        declaredDefault.ShouldBe(Cost(schema, "{ rows(first: 1) { id } }"));
        nullFirst.ShouldBe(defaultPage);
        nullFirst.ShouldBeGreaterThan(declaredDefault);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_ExplicitNullNode_IsNotTheDeclaredDefaultEither()
    {
        // The runtime pass hands the coerced literal nodes to the rule.
        var schema = await SchemaAsync();

        var cost = Cost(schema, "query($f: Int = 1) { rows(first: $f) { id } }", new Dictionary<string, object?> { ["f"] = NullValueNode.Default });

        cost.ShouldBe(Cost(schema, "{ rows(first: 100) { id } }"));
    }

    [Fact]
    public async Task AbsentVariable_UsesDeclaredDefault()
    {
        var schema = await SchemaAsync();

        Cost(schema, "query($f: Int = 7) { rows(first: $f) { id } }")
            .ShouldBe(Cost(schema, "{ rows(first: 7) { id } }"));
        Cost(schema, "query($f: Int = 7) { rows(first: $f) { id } }", new Dictionary<string, object?>())
            .ShouldBe(Cost(schema, "{ rows(first: 7) { id } }"));
    }

    [Fact]
    public async Task VariableWithoutDefault_AndWithoutValue_IsTheFieldsDefaultPage_OnACatalogField()
    {
        var schema = await SchemaAsync();

        Cost(schema, "query($f: Int) { rows(first: $f) { id } }").ShouldBe(Cost(schema, "{ rows(first: 100) { id } }"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_VariableWithoutDefault_OnAnotherField_KeepsTheWorstCase()
    {
        var schema = await SchemaAsync();

        Cost(schema, "query($f: Int) { plain(first: $f) { id } }")
            .ShouldBe(Cost(schema, "{ plain(first: 1000) { id } }"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_UnparsableVariable_IsPricedAtTheWorstCase()
    {
        var schema = await SchemaAsync();

        var cost = Cost(schema, "query($f: Int = 1) { rows(first: $f) { id } }", new Dictionary<string, object?> { ["f"] = "not-a-number" });

        cost.ShouldBe(Cost(schema, "{ rows(first: 1000) { id } }"));
    }

    [Fact]
    public async Task RuntimeValue_IsPricedFromTheCoercedValue()
    {
        var schema = await SchemaAsync();

        Cost(schema, "query($f: Int) { rows(first: $f) { id } }", new Dictionary<string, object?> { ["f"] = 5 })
            .ShouldBe(Cost(schema, "{ rows(first: 5) { id } }"));
        Cost(schema, "query($f: Int) { rows(first: $f) { id } }", new Dictionary<string, object?> { ["f"] = new IntValueNode(5) })
            .ShouldBe(Cost(schema, "{ rows(first: 5) { id } }"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("100000")]
    public async Task OutOfRange_OnACatalogField_IsRejectedAtExecution_PricedAsTheMinimalPage(string first)
    {
        var schema = await SchemaAsync();

        Cost(schema, "{ rows(first: " + first + ") { id } }").ShouldBe(Cost(schema, "{ rows(first: 0) { id } }"));
        Cost(schema, "query($f: Int) { rows(first: $f) { id } }", new Dictionary<string, object?> { ["f"] = int.Parse(first) })
            .ShouldBe(Cost(schema, "{ rows(first: 0) { id } }"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_OutOfRange_OnAnotherField_IsNotDiscounted()
    {
        var schema = await SchemaAsync();

        Cost(schema, "{ plain(first: 100000) { id } }").ShouldBe(Cost(schema, "{ plain(first: 1000) { id } }"));
        Cost(schema, "{ plain(first: -1) { id } }").ShouldBe(Cost(schema, "{ plain(first: 1000) { id } }"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_NullFirstNested_StaysOverBudget()
    {
        var schema = await SchemaAsync();
        const string query = "query($f: Int) { rows(first: $f) { children(first: $f) { children(first: $f) { id } } } }";

        var nullNested = Cost(schema, query, new Dictionary<string, object?> { ["f"] = null });

        nullNested.ShouldBe(Cost(schema, "{ rows(first: 100) { children(first: 100) { children(first: 100) { id } } } }"));
        nullNested.ShouldBeGreaterThan(5000);
    }

    [Fact]
    public async Task OmittedFirst_OnACatalogField_IsTheDefaultPage()
    {
        var schema = await SchemaAsync();

        Cost(schema, "{ rows { id } }").ShouldBe(Cost(schema, "{ rows(first: 100) { id } }"));
    }

    [Theory]
    [InlineData("\"10\"")]
    [InlineData("true")]
    [InlineData("[1]")]
    [InlineData("{ a: 1 }")]
    [InlineData("ASC")]
    public async Task NonIntLiteralPageSize_OnACatalogField_IsATypeErrorNotAComplexityError(string literal)
    {
        // HotChocolate rejects it during validation, so the type error must not be hidden behind QUERY_TOO_COMPLEX.
        var schema = await SchemaAsync();

        Cost(schema, "{ rows(first: " + literal + ") { id } }").ShouldBe(Cost(schema, "{ rows(first: 0) { id } }"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_NonIntLiteralPageSize_OnAnotherField_KeepsTheWorstCase()
    {
        var schema = await SchemaAsync();

        Cost(schema, "{ plain(first: \"10\") { id } }").ShouldBe(Cost(schema, "{ plain(first: 1000) { id } }"));
    }
}
