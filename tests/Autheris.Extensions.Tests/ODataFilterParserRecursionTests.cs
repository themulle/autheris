namespace Autheris.Extensions.Tests;

using System;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Extensions.OData;
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
}
