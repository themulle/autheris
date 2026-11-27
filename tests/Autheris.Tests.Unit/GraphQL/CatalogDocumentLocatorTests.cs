using System.Text;
using Autheris.Api.Middleware;
using Autheris.GraphQL.Catalog;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

[Collection("CatalogRelation")]
public sealed class CatalogDocumentLocatorTests
{
    private static async Task<IRequestExecutor> ExecutorAsync() =>
        await new DisclosureFixture { WithShield = false }.CreateAsync("Production");

    private static async Task<CatalogDocumentLocator?> LocatorAsync(string query)
    {
        var executor = await ExecutorAsync();
        return CatalogDocumentLocator.TryCreate(Utf8GraphQLParser.Parse(query), executor.Schema);
    }

    private static int Col(string query, string token) => query.IndexOf(token, StringComparison.Ordinal) + 1;

    [Fact]
    public async Task UnknownSelectionField_IsLocatedOnOwnerTable()
    {
        const string q = "{ sales_dbo_orders { id secret { id zzz } } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "zzz"), LocatedNodeKind.UnknownField, out var subject).ShouldBeTrue();
        subject.Table.TypeName.ShouldBe("sales_dbo_secrets");
        subject.Field.ShouldBe("zzz");
    }

    [Fact]
    public async Task UnknownFieldUnderConnectionEdgesNode_IsLocatedOnRootTable()
    {
        const string q = "{ sales_dbo_customersConnection(first: 5) { edges { node { id nope } } } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "nope"), LocatedNodeKind.UnknownField, out var subject).ShouldBeTrue();
        subject.Table.TypeName.ShouldBe("sales_dbo_customers");
        subject.Field.ShouldBe("nope");
    }

    [Fact]
    public async Task UnknownWhereColumn_IsUnknownColumnWithArgumentWhere()
    {
        const string q = "{ sales_dbo_customers(first: 5, where: { unk: { eq: 1 } }) { id } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "unk"), LocatedNodeKind.ObjectField, out var subject).ShouldBeTrue();
        subject.IsUnknownColumn.ShouldBeTrue();
        subject.Name.ShouldBe("unk");
        subject.Argument.ShouldBe("where");
        subject.Table.TypeName.ShouldBe("sales_dbo_customers");
    }

    [Fact]
    public async Task UnknownFilterOperator_IsNotAnUnknownColumn()
    {
        const string q = "{ sales_dbo_customers(first: 5, where: { id: { bad: 1 } }) { id } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "bad"), LocatedNodeKind.ObjectField, out var subject).ShouldBeTrue();
        subject.IsUnknownColumn.ShouldBeFalse();
        subject.Field.ShouldBe("sales_dbo_customers");
        subject.Argument.ShouldBe("where");
    }

    [Fact]
    public async Task GroupByToken_IsLocatedWithArgumentGroupBy()
    {
        var field = AggregateTypeNames.For("sales_dbo_customers").Field;
        var q = "{ " + field + "(groupBy: [bogus], aggregates: []) { suppressed } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "bogus"), LocatedNodeKind.Value, out var subject).ShouldBeTrue();
        subject.IsUnknownColumn.ShouldBeTrue();
        subject.Name.ShouldBe("bogus");
        subject.Argument.ShouldBe("groupBy");
        subject.Table.TypeName.ShouldBe("sales_dbo_customers");
    }

    [Fact]
    public async Task MissingRequiredArgument_IsLocatedOnTheKnownField()
    {
        var field = AggregateTypeNames.For("sales_dbo_customers").Field;
        var q = "{ " + field + "(groupBy: [id]) { suppressed } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, 3, LocatedNodeKind.KnownField, out var subject).ShouldBeTrue();
        subject.Argument.ShouldBe("aggregates");
        subject.Field.ShouldBe(field);
    }

    [Fact]
    public async Task RelationTargets_AreInTableSet()
    {
        var locator = await LocatorAsync("{ sales_dbo_orders { id } }");

        locator.ShouldNotBeNull();
        locator.Tables.Select(t => t.TypeName).ShouldBe(["sales_dbo_orders", "sales_dbo_secrets"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("mutation { x }")]
    [InlineData("subscription { x }")]
    [InlineData("{ __schema { types { name } } }")]
    [InlineData("{ unknown_table { id } }")]
    [InlineData("{ sales_dbo_customers { id } unknown_table { id } }")]
    [InlineData("{ __typename }")]
    public async Task MutationOrNonCatalogRoot_ReturnsNull(string query)
    {
        (await LocatorAsync(query)).ShouldBeNull();
    }

    [Fact]
    public async Task FragmentOnWrapperType_ReturnsNull()
    {
        var executor = await ExecutorAsync();
        var wrapper = ((INameProvider)UnwrapType(executor.Schema.QueryType.Fields["sales_dbo_customersConnection"].Type)).Name;
        var q = "fragment F on " + wrapper + " { edges { node { id } } } { sales_dbo_customersConnection { ...F } }";

        CatalogDocumentLocator.TryCreate(Utf8GraphQLParser.Parse(q), executor.Schema).ShouldBeNull();
    }

    [Fact]
    public async Task FragmentOnTableType_IsWalkedOnItsOwn()
    {
        const string q = "fragment F on sales_dbo_customers { id nope } { sales_dbo_customers { ...F } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, Col(q, "nope"), LocatedNodeKind.UnknownField, out var subject).ShouldBeTrue();
        subject.Table.TypeName.ShouldBe("sales_dbo_customers");
    }

    [Fact]
    public async Task InlineFragmentOnOtherType_ReturnsNull()
    {
        (await LocatorAsync("{ sales_dbo_customers { ... on sales_dbo_orders { id } } }")).ShouldBeNull();
    }

    [Fact]
    public async Task VariableUsedInDirective_IsNotLocatable()
    {
        const string q = "query($v: Int, $s: Boolean!) { sales_dbo_customers(first: $v) @include(if: $s) { id } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        locator.TryLocate(1, q.LastIndexOf("$v", StringComparison.Ordinal) + 1, LocatedNodeKind.VariableUsage, out var catalogUsage).ShouldBeTrue();
        catalogUsage.Variable.ShouldBe("v");
        catalogUsage.ExpectedType.ShouldBe("Int");
        locator.TryLocate(1, q.LastIndexOf("$s", StringComparison.Ordinal) + 1, LocatedNodeKind.VariableUsage, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DirectiveAndVariableDefaultNodes_AreNotLocatable()
    {
        const string q = "query($v: Int = 7) { sales_dbo_customers(first: $v) @include(if: nope) { id @skip(if: bad) } }";
        var locator = await LocatorAsync(q);

        locator.ShouldNotBeNull();
        foreach (var token in new[] { "7", "nope", "bad" })
        {
            foreach (var kind in Enum.GetValues<LocatedNodeKind>())
            {
                locator.TryLocate(1, Col(q, token), kind, out _).ShouldBeFalse(token + " " + kind);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WalkDepthAndNodeCaps_ReturnNull()
    {
        var deep = new StringBuilder("{ sales_dbo_customers { ");
        for (var i = 0; i < CatalogDocumentLocator.MaxDepth + 6; i++) deep.Append("... on sales_dbo_customers { ");
        deep.Append("id");
        for (var i = 0; i < CatalogDocumentLocator.MaxDepth + 6; i++) deep.Append(" }");
        deep.Append(" } }");
        (await LocatorAsync(deep.ToString())).ShouldBeNull();

        // The parser caps fields at 2048, so the node cap is reached with many argument values instead.
        var wide = new StringBuilder("{ sales_dbo_customers(orderBy: [");
        for (var i = 0; i < CatalogDocumentLocator.MaxNodes; i++) wide.Append("{ id: ASC } ");
        wide.Append("]) { id } }");
        (await LocatorAsync(wide.ToString())).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MoreThanMaxTables_ReturnsNull()
    {
        var executor = await new DisclosureFixture { WithShield = false, ExtraTables = 34 }.CreateAsync("Production");
        var q = "{ " + string.Join(' ', Enumerable.Range(0, 33).Select(i => $"sales_dbo_extra{i:00} {{ id }}")) + " }";

        CatalogDocumentLocator.TryCreate(Utf8GraphQLParser.Parse(q), executor.Schema).ShouldBeNull();
    }

    private static IType UnwrapType(IType type)
    {
        while (type is IWrapperType wrapper) type = wrapper.InnerType;
        return type;
    }
}
