namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class McpDescribeNarrowingTests
{
    private static McpDatasetDescription Sample(int columnCount = 10) => new(
        Id: "sales.public.orders",
        DisplayName: "Orders",
        Domain: "sales",
        Schema: "public",
        Table: "orders",
        Description: "Orders",
        LongDescription: null,
        Sensitivity: "NORMAL",
        RequiresApproval: false,
        Columns: Enumerable.Range(0, columnCount).Select(i => new McpDatasetColumn($"col_{i}", "text", null, false, i == 0)).ToList(),
        Examples: [new McpDatasetExample("Top orders", null, "{ orders { id } }", null)],
        GraphQl: new McpGraphQlAccess(
            "/graphql", "orders", "Orders", "OrdersFilter", "OrdersOrder", "where, orderBy", "{ orders { id } }",
            [
                new McpGraphQlRelation("customer", "sales.public.customers", false),
                new McpGraphQlRelation("lines", "sales.public.order_lines", true)
            ]),
        PrimaryKeyOrder: [new McpKeyColumn("col_0", "ASC")]);

    private static McpDescribeNarrowing.Request Parse(object? include = null, object? columns = null, int? offset = null, int? limit = null, object? relations = null)
    {
        McpDescribeNarrowing.TryParse(include, columns, offset, limit, relations, out var request).ShouldBeTrue();
        return request;
    }

    [Fact]
    public void Apply_EmptyRequest_ReturnsDescriptionUnchanged()
    {
        var description = Sample();

        McpDescribeNarrowing.Apply(description, Parse()).ShouldBeSameAs(description);
    }

    [Fact]
    public void Apply_IncludeWithoutColumns_DropsColumnsAndKeepsKeysAndGraphQl()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(include: "[\"keys\",\"graphql\",\"relations\"]"));

        result.Columns.ShouldBeEmpty();
        result.ColumnTotal.ShouldBe(10);
        result.PrimaryKeyOrder.ShouldNotBeNull();
        result.GraphQl.ShouldNotBeNull();
        result.GraphQl!.Relations.Count.ShouldBe(2);
        result.Examples.ShouldBeEmpty();
    }

    [Fact]
    public void Apply_IncludeWithoutRelations_EmptiesRelations()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(include: "columns,graphql"));

        result.GraphQl.ShouldNotBeNull();
        result.GraphQl!.Relations.ShouldBeEmpty();
        result.Columns.Count.ShouldBe(10);
    }

    [Fact]
    public void Apply_NamedColumns_ReturnsOnlyThoseColumns()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(columns: "[\"col_2\",\"COL_3\",\"missing\"]"));

        result.Columns.Select(c => c.Name).ShouldBe(["col_2", "col_3"]);
        result.ColumnTotal.ShouldBe(10);
    }

    [Fact]
    public void Apply_ColumnPaging_SkipsAndTakes()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(offset: 4, limit: 3));

        result.Columns.Select(c => c.Name).ShouldBe(["col_4", "col_5", "col_6"]);
        result.ColumnTotal.ShouldBe(10);
    }

    [Fact]
    public void Apply_RelationTerm_FiltersByFieldOrTarget()
    {
        var byField = McpDescribeNarrowing.Apply(Sample(), Parse(relations: "CUSTOMER"));
        var byTarget = McpDescribeNarrowing.Apply(Sample(), Parse(relations: "order_lines"));

        byField.GraphQl!.Relations.Select(r => r.Field).ShouldBe(["customer"]);
        byTarget.GraphQl!.Relations.Select(r => r.Field).ShouldBe(["lines"]);
    }

    [Fact]
    public void Apply_NeverAddsMembers()
    {
        var description = Sample(3);

        var result = McpDescribeNarrowing.Apply(description, Parse(columns: "col_0,col_1,col_2,col_99", offset: 0));

        result.Columns.Count.ShouldBeLessThanOrEqualTo(description.Columns.Count);
        result.Columns.Select(c => c.Name).ShouldAllBe(n => description.Columns.Any(c => c.Name == n));
    }

    private static McpDatasetDescription WithLongDescriptions()
    {
        var d = Sample(2);
        return d with { Columns = d.Columns.Select(c => c with { LongDescription = "long " + c.Name }).ToList() };
    }

    [Fact]
    public void Apply_WithoutLongDescriptionSection_RemovesColumnLongDescriptions()
    {
        var withoutInclude = McpDescribeNarrowing.Apply(WithLongDescriptions(), Parse());
        var withOtherSections = McpDescribeNarrowing.Apply(WithLongDescriptions(), Parse(include: "columns,keys"));

        withoutInclude.Columns.ShouldAllBe(c => c.LongDescription == null);
        withOtherSections.Columns.ShouldAllBe(c => c.LongDescription == null);
    }

    [Fact]
    public void Apply_LongDescriptionSection_KeepsColumnLongDescriptions()
    {
        var result = McpDescribeNarrowing.Apply(WithLongDescriptions(), Parse(include: "columns,longDescription"));

        result.Columns.Select(c => c.LongDescription).ShouldBe(["long col_0", "long col_1"]);
    }

    [Theory]
    [InlineData("secrets")]
    [InlineData("columns,secrets")]
    [InlineData("[1,2]")]
    [InlineData("[\"columns\"")]
    public void TryParse_UnknownOrMalformedInclude_IsRejected(string include) =>
        McpDescribeNarrowing.TryParse(include, null, null, null, null, out _).ShouldBeFalse();

    [Fact]
    public void TryParse_NonStringInclude_IsRejected() =>
        McpDescribeNarrowing.TryParse(5, null, null, null, null, out _).ShouldBeFalse();

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, 501)]
    public void TryParse_OutOfRangePaging_IsRejected(int? offset, int? limit) =>
        McpDescribeNarrowing.TryParse(null, null, offset, limit, null, out _).ShouldBeFalse();

    [Fact]
    public void TryParse_TooManyNames_IsRejected()
    {
        var names = string.Join(',', Enumerable.Range(0, McpDescribeNarrowing.MaxListedNames + 1).Select(i => "c" + i));

        McpDescribeNarrowing.TryParse(null, names, null, null, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Apply_RelationsWithoutGraphQl_ReturnsGraphQlSectionWithOnlyRelations()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(include: "relations"));

        result.GraphQl.ShouldNotBeNull();
        result.GraphQl!.Relations.Select(r => r.Field).ShouldBe(["customer", "lines"]);
        result.GraphQl.Endpoint.ShouldBeNull();
        result.GraphQl.QueryField.ShouldBeNull();
        result.GraphQl.TypeName.ShouldBeNull();
        result.GraphQl.FilterType.ShouldBeNull();
        result.GraphQl.OrderByType.ShouldBeNull();
        result.GraphQl.Arguments.ShouldBeNull();
        result.GraphQl.ExampleQuery.ShouldBeNull();
        result.GraphQl.Variables.ShouldBeNull();
        result.GraphQl.Aggregate.ShouldBeNull();
        result.GraphQl.Connection.ShouldBeNull();
    }

    [Fact]
    public void Apply_RelationsWithoutGraphQl_AppliesRelationTerm()
    {
        var result = McpDescribeNarrowing.Apply(Sample(), Parse(include: "relations", relations: "line"));

        result.GraphQl!.Relations.Select(r => r.Field).ShouldBe(["lines"]);
    }

    [Fact]
    public void Apply_NeitherGraphQlNorRelations_DropsGraphQl()
    {
        McpDescribeNarrowing.Apply(Sample(), Parse(include: "columns,keys")).GraphQl.ShouldBeNull();
    }

    [Fact]
    public void Request_WantsRelations_IndependentOfGraphQl()
    {
        Parse(include: "relations").WantsRelations.ShouldBeTrue();
        Parse(include: "relations").WantsGraphQl.ShouldBeFalse();
        Parse(include: "relations").WantsGraphQlBlock.ShouldBeTrue();
        Parse(include: "graphql").WantsRelations.ShouldBeFalse();
        Parse(include: "graphql").WantsGraphQlBlock.ShouldBeTrue();
        Parse(include: "columns").WantsGraphQlBlock.ShouldBeFalse();
        Parse().WantsRelations.ShouldBeTrue();
    }

    [Theory]
    [InlineData("graphql", true)]
    [InlineData("relations", true)]
    [InlineData("columns,keys", false)]
    public void Request_ConflictsWithBrief_OnlyForGraphQlOrRelations(string include, bool expected)
    {
        Parse(include: include).ConflictsWithBrief.ShouldBe(expected);
        Parse().ConflictsWithBrief.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TryParse_UnknownInclude_StillRejected_WithRelations()
    {
        McpDescribeNarrowing.TryParse("relations,joins", null, null, null, null, out _).ShouldBeFalse();
        McpDescribeNarrowing.TryParse(42, null, null, null, null, out _).ShouldBeFalse();
    }
}
