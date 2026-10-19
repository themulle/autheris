using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate;
using HotChocolate.Language;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>#112 A-2, C-4, T-5.5, T-5.6, T-5.12: argument shape rules before the engine is called.</summary>
public sealed class AggregateArgumentParserTests
{
    private static readonly TableIdentifier Id = new("sales", "dbo", "orders");

    private static readonly CatalogTableType Table = new(
        Id, "sales_dbo_orders", "sales_dbo_orders", "f", "o",
        new TableMetadata { Identifier = Id, Table = new Table { IsActive = true } },
        [
            new CatalogColumnField("region", "Region", CatalogFieldType.String, "varchar", true),
            new CatalogColumnField("amount", "Amount", CatalogFieldType.Decimal, "decimal", true),
            new CatalogColumnField("qty", "Qty", CatalogFieldType.Int, "int", true),
            new CatalogColumnField("flag", "Flag", CatalogFieldType.Boolean, "bit", true),
            new CatalogColumnField("created", "Created", CatalogFieldType.DateTime, "datetime", true),
            new CatalogColumnField("true", "TrueCol", CatalogFieldType.String, "varchar", true)
        ],
        []);

    private static IValueNode? Lit(string? text) => text is null ? null : Utf8GraphQLParser.Syntax.ParseValueLiteral(text, false);

    private static ParsedAggregateArguments Parse(
        string? aggregates,
        string? groupBy = null,
        string? orderBy = null,
        string? limit = null,
        AggregationOptions? options = null,
        Func<string, IValueNode?>? variable = null) =>
        AggregateArgumentParser.Parse(Table, Lit(groupBy), Lit(aggregates), Lit(orderBy), Lit(limit), options ?? new AggregationOptions(), variable ?? (_ => null));

    private static string CodeOf(Action act) => Should.Throw<GraphQLException>(act).Errors.Single().Code!;

    [Fact]
    public void Parse_HappyPath_MapsColumnsFunctionsKindsAndOrder()
    {
        var parsed = Parse(
            "[{ fn: SUM, column: amount, alias: \"total\" }, { fn: COUNT, alias: \"n\" }]",
            groupBy: "[region]",
            orderBy: "[{ alias: \"total\", direction: DESC }, { column: region }]",
            limit: "25");

        parsed.GroupBy.Select(c => c.ColumnName).ShouldBe(["Region"]);
        parsed.Aggregates.Select(a => (a.Alias, a.Function, a.Kind)).ShouldBe(
        [
            ("total", AggregateFunction.Sum, AggregateValueKind.Decimal),
            ("n", AggregateFunction.Count, AggregateValueKind.Int)
        ]);
        parsed.Aggregates[0].Column!.ColumnName.ShouldBe("Amount");
        parsed.OrderBy.ShouldBe([new TableOrderBy("total", true), new TableOrderBy("Region", false)]);
        parsed.Limit.ShouldBe(25);
    }

    [Fact]
    public void Parse_Defaults_NoGroupBy_NoOrder_LimitHundred()
    {
        var parsed = Parse("[{ fn: COUNT, alias: \"n\" }]");

        parsed.GroupBy.ShouldBeEmpty();
        parsed.OrderBy.ShouldBeEmpty();
        parsed.Limit.ShouldBe(100);
    }

    [Fact]
    public void Parse_ReservedEnumValues_AreMappedBackToTheColumn()
    {
        var parsed = Parse("[{ fn: COUNT, alias: \"n\" }]", groupBy: "[true_col]");

        parsed.GroupBy.Single().ColumnName.ShouldBe("TrueCol");
    }

    [Fact]
    public void Parse_UsesVariablesForArguments()
    {
        var parsed = Parse(
            "$aggs",
            groupBy: "$cols",
            limit: "$lim",
            variable: name => name switch
            {
                "aggs" => Lit("[{ fn: MAX, column: qty, alias: \"m\" }]"),
                "cols" => Lit("[region]"),
                "lim" => Lit("7"),
                _ => null
            });

        parsed.Aggregates.Single().Kind.ShouldBe(AggregateValueKind.Int);
        parsed.GroupBy.Single().FieldName.ShouldBe("region");
        parsed.Limit.ShouldBe(7);
    }

    [Theory]
    [InlineData("bad alias")]
    [InlineData("1abc")]
    [InlineData("")]
    [InlineData("count")]
    [InlineData("COUNT")]
    [InlineData("__n")]
    [InlineData("__x")]
    [InlineData("region")]
    [InlineData("Region")]
    [InlineData("a-b")]
    public void Parse_InvalidAlias_IsRejectedWithAFixedCode(string alias)
    {
        var code = CodeOf(() => Parse($"[{{ fn: COUNT, alias: \"{alias}\" }}]", groupBy: "[region]"));

        code.ShouldBe(AggregationErrorCodes.AliasInvalid);
    }

    [Fact]
    public void Parse_AliasLongerThan64_IsRejected() =>
        CodeOf(() => Parse($"[{{ fn: COUNT, alias: \"{new string('a', 65)}\" }}]")).ShouldBe(AggregationErrorCodes.AliasInvalid);

    [Fact]
    public void Parse_DuplicateAlias_CaseInsensitive_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"n\" }, { fn: SUM, column: qty, alias: \"N\" }]")).ShouldBe(AggregationErrorCodes.AliasInvalid);

    [Trait("Category", "Security")]
    [Fact]
    public void Parse_DuplicateFunctionColumnPair_IsRejected_C4() =>
        CodeOf(() => Parse("[{ fn: SUM, column: qty, alias: \"a\" }, { fn: SUM, column: qty, alias: \"b\" }]")).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Theory]
    [InlineData("SUM", "flag")]
    [InlineData("AVG", "flag")]
    [InlineData("MIN", "flag")]
    [InlineData("AVG", "region")]
    [InlineData("SUM", "created")]
    [InlineData("SUM", null)]
    [InlineData("COUNT_DISTINCT", null)]
    public void Parse_InvalidFunctionForColumnType_IsRejected(string fn, string? column)
    {
        var col = column is null ? string.Empty : $", column: {column}";

        CodeOf(() => Parse($"[{{ fn: {fn}{col}, alias: \"x\" }}]")).ShouldBe(AggregationErrorCodes.InvalidFunctionForType);
    }

    [Fact]
    public void Parse_NoAggregates_IsRejected() =>
        CodeOf(() => Parse("[]")).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Fact]
    public void Parse_TooManyAggregates_IsRejected()
    {
        var options = new AggregationOptions { MaxAggregates = 2 };

        CodeOf(() => Parse("[{ fn: COUNT, alias: \"a\" }, { fn: SUM, column: qty, alias: \"b\" }, { fn: MAX, column: qty, alias: \"c\" }]", options: options))
            .ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public void Parse_TooManyGroupByColumns_IsRejected()
    {
        var options = new AggregationOptions { MaxGroupByColumns = 1 };

        CodeOf(() => Parse("[{ fn: COUNT, alias: \"a\" }]", groupBy: "[region, qty]", options: options)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);
    }

    [Fact]
    public void Parse_DuplicateGroupByColumn_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"a\" }]", groupBy: "[region, region]")).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void Parse_NonPositiveLimit_IsRejected(string limit) =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"a\" }]", limit: limit)).ShouldBe(AggregationErrorCodes.ArgumentInvalid);

    [Fact]
    public void Parse_LimitAboveTheUpperBound_IsRejectedAsLimitExceeded() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"a\" }]", limit: (AggregationOptions.UpperBoundMaxGroups + 1).ToString()))
            .ShouldBe(AggregationErrorCodes.LimitExceeded);

    [Fact]
    public void Parse_LimitAboveTheConfiguredCapButBelowTheUpperBound_IsPassedToTheEngine() =>
        Parse("[{ fn: COUNT, alias: \"a\" }]", limit: "20000").Limit.ShouldBe(20000);

    [Fact]
    public void Parse_OrderByWithBothColumnAndAlias_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"n\" }]", groupBy: "[region]", orderBy: "[{ column: region, alias: \"n\" }]"))
            .ShouldBe(AggregationErrorCodes.OrderInvalid);

    [Fact]
    public void Parse_OrderByWithNeitherColumnNorAlias_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"n\" }]", groupBy: "[region]", orderBy: "[{ direction: DESC }]"))
            .ShouldBe(AggregationErrorCodes.OrderInvalid);

    [Fact]
    public void Parse_OrderByUnknownAlias_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"n\" }]", orderBy: "[{ alias: \"nope\" }]")).ShouldBe(AggregationErrorCodes.OrderInvalid);

    [Fact]
    public void Parse_OrderByColumnNotInGroupBy_IsRejected() =>
        CodeOf(() => Parse("[{ fn: COUNT, alias: \"n\" }]", groupBy: "[region]", orderBy: "[{ column: qty }]")).ShouldBe(AggregationErrorCodes.OrderInvalid);

    [Trait("Category", "Security")]
    [Fact]
    public void Parse_Errors_NeverEchoTheRequestedValue_SEC_112_11()
    {
        var ex = Should.Throw<GraphQLException>(() => Parse("[{ fn: COUNT, alias: \"secret bad alias\" }]", groupBy: "[region]"));

        ex.Errors.Single().Message.ShouldNotContain("secret");
        ex.Errors.Single().Message.ShouldNotContain("region");
    }

    [Fact]
    public void ColumnEnum_NamesAreTheFieldNames_ReservedWordsGetASuffix()
    {
        var map = AggregateColumnEnum.Build(Table);

        map.Select(m => m.EnumValue).ShouldBe(["region", "amount", "qty", "flag", "created", "true_col"]);
        map.Select(m => m.EnumValue).Distinct(StringComparer.Ordinal).Count().ShouldBe(map.Count);
    }

    [Fact]
    public void ColumnEnum_SuffixedNameNeverCollidesWithAnotherField()
    {
        var cols = new[]
        {
            new CatalogColumnField("null", "N", CatalogFieldType.String, "varchar", true),
            new CatalogColumnField("null_col", "NC", CatalogFieldType.String, "varchar", true)
        };
        var table = Table with { Columns = cols };

        var names = AggregateColumnEnum.Build(table).Select(m => m.EnumValue).ToList();

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(2);
        names.ShouldContain("null_col");
        names.ShouldNotContain("null");
    }
}
