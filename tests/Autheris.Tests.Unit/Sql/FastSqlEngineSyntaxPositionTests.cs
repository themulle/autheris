namespace Autheris.Tests.Unit.Sql;

using System;
using System.Threading;
using Antlr4.Runtime.Misc;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Policy;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class FastSqlEngineSyntaxPositionTests
{
    private static readonly SqlTokenSecurityOptions StrictOptions = new()
    {
        RejectComments = true,
        AllowTrailingStatementTerminator = false
    };

    [Fact]
    public void SyntaxError_CarriesLineAndColumn()
    {
        var ex = Should.Throw<SqlSyntaxErrorException>(() =>
            FastSqlEngine.Default.Parse("SELECT\n  FROM".AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex.Line.ShouldBe(2);
        ex.Column.ShouldBe(3);
    }

    [Fact]
    public void NegativeCacheHit_KeepsPosition()
    {
        const string BadSql = "SELECT\n  FROM";
        var ex1 = Should.Throw<SqlSyntaxErrorException>(() =>
            FastSqlEngine.Default.Parse(BadSql.AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex1.Line.ShouldBe(2);
        ex1.Column.ShouldBe(3);

        var ex2 = Should.Throw<SqlSyntaxErrorException>(() =>
            FastSqlEngine.Default.Parse(BadSql.AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex2.Line.ShouldBe(2);
        ex2.Column.ShouldBe(3);
    }

    [Fact]
    public void TrailingSemicolon_Accepted_WhenAllowed()
    {
        var (tree, _) = FastSqlEngine.Default.Parse("SELECT 1;".AsMemory(), ClientSqlTokenPolicy.Analysis);
        tree.ShouldNotBeNull();
    }

    [Fact]
    public void TrailingSemicolonThenComment_Accepted()
    {
        var (tree, _) = FastSqlEngine.Default.Parse("SELECT 1; -- x".AsMemory(), ClientSqlTokenPolicy.Analysis);
        tree.ShouldNotBeNull();
    }

    [Fact]
    public void TrailingSemicolon_Rejected_WhenNotAllowed()
    {
        Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse("SELECT 1;".AsMemory(), StrictOptions));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TwoSemicolons_Rejected_AtSecond()
    {
        var ex = Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse("SELECT 1;;".AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex.ShouldBeOfType<SqlSyntaxErrorException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SecondStatement_Rejected_AtSecondStatement()
    {
        var ex = Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse("SELECT 1; SELECT 2".AsMemory(), ClientSqlTokenPolicy.Analysis));

        ex.ShouldBeOfType<SqlSyntaxErrorException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PolicyDeniedColumn_HasNoPosition()
    {
        var ex = new WebSqlPolicyException("Column access denied");
        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Line.ShouldBeNull();
        mapping.Column.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void UnknownTable_HasNoPosition()
    {
        var ex = new PolicyParseException("Table not found");
        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Line.ShouldBeNull();
        mapping.Column.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ParseTimeout_HasNoPosition()
    {
        var ex = new ParseCanceledException("SQL parsing exceeded the time budget.", new TimeoutException("budget"));
        ex.ShouldNotBeOfType<SqlSyntaxErrorException>();
        SqlCompilerErrorMapper.TryMap(ex, out var mapping).ShouldBeTrue();
        mapping.Line.ShouldBeNull();
        mapping.Column.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void StrictOptions_NotServedFromLenientNegativeCacheEntry()
    {
        // First parse under client options with trailing semicolon: succeeds
        var (tree, _) = FastSqlEngine.Default.Parse("SELECT 42;".AsMemory(), ClientSqlTokenPolicy.Analysis);
        tree.ShouldNotBeNull();

        // Second parse of same SQL under strict options: must fail closed and reject
        Should.Throw<ParseCanceledException>(() =>
            FastSqlEngine.Default.Parse("SELECT 42;".AsMemory(), StrictOptions));
    }
}
