using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-8: the typed metric filter, order and argument rules (T-5.13, T-5.15, T-S-15).</summary>
public sealed class MetricRequestParserTests
{
    private static IValueNode? Lit(string? text) => text is null ? null : Utf8GraphQLParser.Syntax.ParseValueLiteral(text, false);

    private static ParsedMetricArguments Parse(
        string metrics = "[\"revenue\"]",
        string? groupBy = null,
        string? where = null,
        string? granularity = null,
        string? orderBy = null,
        string? limit = null,
        Func<string, IValueNode?>? variable = null) =>
        MetricRequestParser.Parse(Lit(metrics), Lit(groupBy), Lit(where), Lit(granularity), Lit(orderBy), Lit(limit), new AggregationOptions(), variable ?? (_ => null));

    private static string CodeOf(Action act) => Should.Throw<GraphQLException>(act).Errors.Single().Code!;

    [Fact]
    public void Parse_Basics_MapsMetricsGroupByGranularityOrderAndLimit()
    {
        var parsed = Parse(
            "[\"revenue\", \"orders\"]",
            groupBy: "[\"region\"]",
            granularity: "\"month\"",
            orderBy: "[{ metric: \"revenue\", direction: DESC }, { dimension: \"region\" }]",
            limit: "10");

        parsed.Metrics.ShouldBe(["revenue", "orders"]);
        parsed.GroupBy.ShouldBe(["region"]);
        parsed.TimeGranularity.ShouldBe("month");
        parsed.OrderBy.ShouldBe([new TableOrderBy("revenue", true), new TableOrderBy("region", false)]);
        parsed.Limit.ShouldBe(10);
        parsed.Where.ShouldBeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Parse_NoMetric_IsRejected(string metrics) =>
        CodeOf(() => Parse(metrics)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Fact]
    public void Parse_TooManyMetrics_IsRejected()
    {
        var many = "[" + string.Join(", ", Enumerable.Range(0, new AggregationOptions().MaxAggregates + 1).Select(i => $"\"m{i}\"")) + "]";

        CodeOf(() => Parse(many)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public void Parse_TooManyGroupByColumns_IsRejected() =>
        CodeOf(() => Parse(groupBy: "[\"a\",\"b\",\"c\",\"d\",\"e\"]")).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Fact]
    public void Parse_LimitRules()
    {
        CodeOf(() => Parse(limit: "0")).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
        CodeOf(() => Parse(limit: (AggregationOptions.UpperBoundMaxGroups + 1).ToString())).ShouldBe(AggregationErrorCodes.LimitExceeded);
        Parse(limit: "null").Limit.ShouldBeNull();
    }

    [Theory]
    [InlineData("[{ metric: \"a\", dimension: \"b\" }]")]
    [InlineData("[{ direction: ASC }]")]
    public void Parse_OrderByNeedsExactlyOneTarget(string orderBy) =>
        CodeOf(() => Parse(orderBy: orderBy)).ShouldBe(AggregationErrorCodes.OrderInvalid);

    [Fact]
    public void Where_StringLeaf_BecomesAComparisonWithDeclaredKind()
    {
        var parsed = Parse(where: "{ dimension: { name: \"region\", where: { string: { eq: \"EU\" } } } }");

        var leaf = parsed.Where.ShouldBeOfType<TreeComparison>();
        leaf.Column.ShouldBe("region");
        leaf.Operator.ShouldBe(TreeFilterOperator.Eq);
        leaf.Value.ShouldBe("EU");
        leaf.DeclaredKind.ShouldBe(ColumnValueKind.String);
    }

    [Theory]
    [InlineData("int", "{ gte: 5 }", TreeFilterOperator.Gte, ColumnValueKind.Int)]
    [InlineData("long", "{ lt: 5 }", TreeFilterOperator.Lt, ColumnValueKind.Long)]
    [InlineData("float", "{ gt: 1.5 }", TreeFilterOperator.Gt, ColumnValueKind.Float)]
    [InlineData("decimal", "{ neq: 1.5 }", TreeFilterOperator.Neq, ColumnValueKind.Decimal)]
    [InlineData("boolean", "{ eq: true }", TreeFilterOperator.Eq, ColumnValueKind.Boolean)]
    [InlineData("dateTime", "{ lte: \"2026-01-01T00:00:00Z\" }", TreeFilterOperator.Lte, ColumnValueKind.DateTime)]
    [InlineData("string", "{ contains: \"x\" }", TreeFilterOperator.Contains, ColumnValueKind.String)]
    [InlineData("string", "{ startsWith: \"x\" }", TreeFilterOperator.StartsWith, ColumnValueKind.String)]
    [InlineData("string", "{ endsWith: \"x\" }", TreeFilterOperator.EndsWith, ColumnValueKind.String)]
    [InlineData("string", "{ isNull: true }", TreeFilterOperator.IsNull, ColumnValueKind.String)]
    [InlineData("string", "{ in: [\"a\", \"b\"] }", TreeFilterOperator.In, ColumnValueKind.String)]
    [InlineData("int", "{ nin: [1, 2] }", TreeFilterOperator.NotIn, ColumnValueKind.Int)]
    public void Where_EveryLeafKind_SetsTheDeclaredKind(string leaf, string ops, TreeFilterOperator op, ColumnValueKind kind)
    {
        var parsed = Parse(where: $"{{ dimension: {{ name: \"d\", where: {{ {leaf}: {ops} }} }} }}");

        var comparison = parsed.Where.ShouldBeOfType<TreeComparison>();
        comparison.Operator.ShouldBe(op);
        comparison.DeclaredKind.ShouldBe(kind);
    }

    [Fact]
    public void Where_AndOrNot_AreNested_AndEveryLeafKeepsItsKind()
    {
        var parsed = Parse(where: """
            { and: [
                { dimension: { name: "a", where: { int: { gt: 1 } } } },
                { or: [ { dimension: { name: "b", where: { string: { eq: "x" } } } }, { not: { dimension: { name: "c", where: { boolean: { eq: false } } } } } ] }
              ] }
            """);

        var and = parsed.Where.ShouldBeOfType<TreeAndFilter>();
        and.Items.Count.ShouldBe(2);
        var or = and.Items[1].ShouldBeOfType<TreeOrFilter>();
        or.Items[0].ShouldBeOfType<TreeComparison>().DeclaredKind.ShouldBe(ColumnValueKind.String);
        or.Items[1].ShouldBeOfType<TreeNotFilter>().Item.ShouldBeOfType<TreeComparison>().DeclaredKind.ShouldBe(ColumnValueKind.Boolean);
    }

    [Fact]
    public void Where_SeveralOperatorsOnOneLeaf_AreAnded()
    {
        var parsed = Parse(where: "{ dimension: { name: \"a\", where: { int: { gte: 1, lt: 9 } } } }");

        parsed.Where.ShouldBeOfType<TreeAndFilter>().Items.Count.ShouldBe(2);
    }

    [Fact]
    public void Where_Variables_AreResolved()
    {
        var parsed = Parse(
            where: "$w",
            variable: name => name == "w" ? Lit("{ dimension: { name: \"region\", where: { string: { eq: \"EU\" } } } }") : null);

        parsed.Where.ShouldBeOfType<TreeComparison>().Value.ShouldBe("EU");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("{ dimension: { name: \"a\", where: { } } }")]
    [InlineData("{ dimension: { name: \"a\", where: { int: { eq: 1 }, string: { eq: \"x\" } } } }")]
    [InlineData("{ dimension: { name: \"a\", where: { int: { } } } }")]
    [InlineData("{ dimension: { name: \"a\", where: { int: { eq: \"text\" } } } }")]
    [InlineData("{ dimension: { name: \"a\", where: { int: { isNull: 1 } } } }")]
    [InlineData("{ dimension: { name: \"\", where: { int: { eq: 1 } } } }")]
    [InlineData("{ dimension: { where: { int: { eq: 1 } } } }")]
    [InlineData("{ unknown: 1 }")]
    [InlineData("{ }")]
    public void Where_MalformedLeaf_IsRejected_WithAFixedCode(string where)
    {
        var parsed = () => Parse(where: where);

        var code = Should.Throw<GraphQLException>(parsed).Errors.Single().Code;
        code.ShouldBeOneOf(AggregationErrorCodes.MetricFilterInvalid, AggregationErrorCodes.ArgumentInvalid);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Where_MalformedLeaf_NeverEchoesTheValue()
    {
        var ex = Should.Throw<GraphQLException>(() => Parse(where: "{ dimension: { name: \"secret_dim\", where: { int: { eq: \"secret-text\" } } } }"));

        ex.Errors.Single().Message.ShouldNotContain("secret");
    }

    [Fact]
    public void Where_NestedDeeperThanTheFilterDepth_IsRejected()
    {
        var where = "{ dimension: { name: \"a\", where: { int: { eq: 1 } } } }";
        for (var i = 0; i <= TreeSqlCompiler.MaxFilterDepth + 1; i++)
        {
            where = $"{{ not: {where} }}";
        }

        CodeOf(() => Parse(where: where)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }
}
