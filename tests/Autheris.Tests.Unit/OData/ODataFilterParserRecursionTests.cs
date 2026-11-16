namespace Autheris.Tests.Unit.OData;

using System;
using System.Linq;
using System.Text;
using Autheris.Application.OData;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Shouldly;
using Xunit;

public sealed class ODataFilterParserRecursionTests
{
    [Fact]
    public void Filter_String_Exceeding_MaxLength_Throws_GatewayInvalidQueryException()
    {
        // 4097 characters
        var longFilter = "status eq '" + new string('a', 4090) + "'";
        longFilter.Length.ShouldBeGreaterThan(ODataFilterParser.MaxFilterLength);

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(longFilter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("exceeds maximum allowed length");
    }

    [Fact]
    public void Deeply_Nested_Parentheses_Exceeding_MaxDepth_Throws_GatewayInvalidQueryException()
    {
        // 40 nested parentheses > 32
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append('(');
        sb.Append("status eq 'active'");
        for (int i = 0; i < 40; i++) sb.Append(')');

        var filter = sb.ToString();

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("maximum recursion depth");
    }

    [Fact]
    public void Deeply_Nested_Functions_Exceeding_MaxDepth_Throws_GatewayInvalidQueryException()
    {
        // tolower(tolower(...)) 35 times
        var sb = new StringBuilder();
        for (int i = 0; i < 35; i++) sb.Append("tolower(");
        sb.Append("status");
        for (int i = 0; i < 35; i++) sb.Append(')');
        sb.Append(" eq 'active'");

        var filter = sb.ToString();

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("maximum recursion depth");
    }

    [Fact]
    public void Deeply_Nested_Not_Exceeding_MaxDepth_Throws_GatewayInvalidQueryException()
    {
        // not not not ... 35 times
        var sb = new StringBuilder();
        for (int i = 0; i < 35; i++) sb.Append("not ");
        sb.Append("(status eq 'active')");

        var filter = sb.ToString();

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("maximum recursion depth");
    }

    [Fact]
    public void Valid_Nested_Filter_Under_MaxDepth_Succeeds()
    {
        // 10 nested parentheses (< 32)
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append('(');
        sb.Append("status eq 'active'");
        for (int i = 0; i < 10; i++) sb.Append(')');

        var clause = ODataFilterParser.Parse(sb.ToString(), DatabaseDialect.PostgreSql);
        clause.ShouldNotBeNull();
        clause.ReferencedColumns.ShouldContain("status");
    }

    [Fact]
    public void Or_Chain_Of_200_Terms_Is_Accepted()
    {
        var filter = string.Join(" or ", Enumerable.Range(0, 200).Select(i => $"c{i} eq {i}"));
        filter.Length.ShouldBeLessThanOrEqualTo(ODataFilterParser.MaxFilterLength);

        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ShouldNotBeNull();
        clause.Parameters.Count.ShouldBe(200);
        clause.ReferencedColumns.Count.ShouldBe(200);
        clause.ReferencedColumns.ShouldContain("c199");
    }

    [Fact]
    public void And_Chain_Within_MaxFilterLength_Is_Accepted()
    {
        var filter = string.Join(" and ", Enumerable.Range(0, 200).Select(i => $"c{i} eq {i}"));
        filter.Length.ShouldBeLessThanOrEqualTo(ODataFilterParser.MaxFilterLength);

        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ShouldNotBeNull();
        clause.Parameters.Count.ShouldBe(200);
        clause.ReferencedColumns.Count.ShouldBe(200);
        clause.ReferencedColumns.ShouldContain("c199");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Nested_Parentheses_Beyond_Limit_Are_Rejected()
    {
        var depth = ODataFilterParser.MaxRecursionDepth + 2;
        var filter = new string('(', depth) + "status eq 'active'" + new string(')', depth);

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("maximum recursion depth");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Not_Nesting_Beyond_Limit_Is_Rejected()
    {
        var depth = ODataFilterParser.MaxRecursionDepth + 2;
        var filter = string.Concat(Enumerable.Repeat("not ", depth)) + "(status eq 'active')";

        var ex = Should.Throw<GatewayInvalidQueryException>(() =>
            ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql));

        ex.Message.ShouldContain("maximum recursion depth");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ChainNode_CollectReferencedColumns_Returns_All_Columns()
    {
        var filter = "col_a eq 1 or col_b eq 2 or col_c eq 3 and col_d eq 4";

        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ReferencedColumns.ShouldBe(["col_a", "col_b", "col_c", "col_d"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OData_Chain_CollectReferencedColumns_Includes_Every_Operand()
    {
        var filter = string.Join(" or ", Enumerable.Range(0, 200).Select(i => $"col_{i} eq {i}"));
        filter.Length.ShouldBeLessThanOrEqualTo(ODataFilterParser.MaxFilterLength);

        var clause = ODataFilterParser.Parse(filter, DatabaseDialect.PostgreSql);

        clause.ReferencedColumns.Count.ShouldBe(200);
        for (int i = 0; i < 200; i++)
        {
            clause.ReferencedColumns.ShouldContain($"col_{i}");
        }
    }

    [Fact]
    public void Or_Chain_Sql_And_Parameters_Match_Left_Associative_Nesting()
    {
        var clause = ODataFilterParser.Parse("a eq 1 or b eq 2 or c eq 3 or d eq 4", DatabaseDialect.PostgreSql, "@p");

        clause.SqlPredicate.ShouldBe("((((\"a\" = @p0) OR (\"b\" = @p1)) OR (\"c\" = @p2)) OR (\"d\" = @p3))");
        clause.Parameters.Count.ShouldBe(4);
        clause.Parameters["@p0"].ShouldBe(1m);
        clause.Parameters["@p1"].ShouldBe(2m);
        clause.Parameters["@p2"].ShouldBe(3m);
        clause.Parameters["@p3"].ShouldBe(4m);
    }
}
