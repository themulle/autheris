namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Linq;
using Autheris.Application.VirtualFilters;
using Shouldly;
using Xunit;

/// <summary>PLAN-GOV-129 test 19 (A-4, BC-129-05): the rewrite of <c>{{caller.x}}</c> to the reserved sentinel and what it refuses.</summary>
[Trait("Category", "Security")]
public sealed class CallerPlaceholderTests
{
    [Fact]
    public void Scalar_AndInForm_AreRewritten_AndTheNamesListed()
    {
        var sql = CallerPlaceholders.Rewrite("from t where target.a = {{caller.cost_center}} and target.b in ({{caller.regions}})", out var names);
        sql.ShouldBe("from t where target.a = '__autheris_caller__cost_center' and target.b in ('__autheris_caller__regions')");
        names.ShouldBe(["cost_center", "regions"]);
    }

    [Fact]
    public void SameAttributeTwice_IsListedOnce()
    {
        CallerPlaceholders.Rewrite("where a = {{caller.x}} and b = {{caller.x}}", out var names);
        names.ShouldBe(["x"]);
    }

    [Fact]
    public void NoPlaceholder_IsUnchanged()
    {
        CallerPlaceholders.Rewrite("from t where target.a = 'x'", out var names).ShouldBe("from t where target.a = 'x'");
        names.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("where a = '{{caller.x}}'")]                                    // quoted
    [InlineData("where a = 'prefix {{caller.x}}'")]                             // inside a literal
    [InlineData("where a = '__autheris_caller__x'")]                            // the sentinel spelled by an author
    [InlineData("where a = '__AUTHERIS_CALLER__x'")]
    [InlineData("where __autheris_caller__x = 1")]                              // identifier
    [InlineData("where a = {{caller.X}}")]                                      // upper case
    [InlineData("where a = {{CALLER.x}}")]
    [InlineData("where a = {{caller.}}")]
    [InlineData("where a = {{caller.x")]
    [InlineData("where a = {{caller.1x}}")]
    [InlineData("where a = {{caller.a__b}}")]
    [InlineData("where a = {{ caller.x }}")]
    [InlineData("where a = {{tenant}}")]                                        // only the quoted tenant value exists
    public void ForbiddenSpellings_AreRefused(string sql) =>
        Should.Throw<ArgumentException>(() => CallerPlaceholders.Rewrite(sql, out _));

    [Fact]
    public void TheQuotedTenantPlaceholder_IsNotACallerPlaceholder()
    {
        CallerPlaceholders.Rewrite("where c.tenant_id = '{{tenant}}'", out var names).ShouldBe("where c.tenant_id = '{{tenant}}'");
        names.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("where a = {{caller.x}}", true)]
    [InlineData("where a = '__autheris_caller__x'", true)]
    [InlineData("where a = '{{CALLER.x}}'", true)]
    [InlineData("where a = 1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Contains_FindsEveryMention(string? sql, bool expected) => CallerPlaceholders.Contains(sql).ShouldBe(expected);

    [Fact]
    public void WithHarmlessValues_RemovesEverySentinel()
    {
        var text = CallerPlaceholders.WithHarmlessValues(CallerPlaceholders.Rewrite("where a = {{caller.x}} and b in ({{caller.y}})", out _));
        text.ShouldNotContain("__autheris_caller__");
    }
}
