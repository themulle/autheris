using System.Text.RegularExpressions;
using Autheris.Application.Governance;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-PF-03 / SEC-SC-20 (M6, L5): object-pattern matching is cached per raw pattern and unevaluable patterns resolve in the restrictive polarity.</summary>
[Collection("CasbinObjectPattern")]
public sealed class CasbinObjectPatternTests : IDisposable
{
    public void Dispose() => CasbinEnforcementService.RegexIsMatch = static (regex, target) => regex.IsMatch(target);

    [Theory]
    [InlineData("finance.dbo.*", "finance.dbo.salaries", true)]
    [InlineData("finance.dbo.*", "finance.dbo_hr.salaries", false)]
    [InlineData("finance.dbo.*", "finance.dbo", false)]
    [InlineData("finance.*.salaries", "finance.dbo.salaries", true)]
    [InlineData("finance.*.salaries", "finance.a.b.salaries", false)]
    [InlineData("sales.:schema.orders", "sales.crm.orders", true)]
    [InlineData("FINANCE.DBO.*", "finance.dbo.salaries", true)]
    [InlineData("finance.dbo.salaries", "finance.dbo.other", false)]
    public void SEC_SC_20_MatchObjectPattern_KeepsItsSemantics_AcrossRepeatedCachedCalls(string pattern, string target, bool expected)
    {
        for (var i = 0; i < 3; i++)
        {
            CasbinEnforcementService.MatchObjectPattern(pattern, target).ShouldBe(expected);
        }
    }

    [Fact]
    public void SEC_SC_20_RegexTimeout_IsRestrictive_ADenyRuleMatches_AnAllowRuleDoesNot()
    {
        CasbinEnforcementService.RegexIsMatch = static (_, _) => throw new RegexMatchTimeoutException();

        CasbinEnforcementService.MatchObjectPattern("sec.*.hr_timeout", "sec.a.hr_timeout", failClosedMatch: true).ShouldBeTrue();
        CasbinEnforcementService.MatchObjectPattern("sec.*.hr_timeout", "sec.a.hr_timeout", failClosedMatch: false).ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_20_RawPatternIsCompiledOnce()
    {
        var calls = 0;
        CasbinEnforcementService.RegexIsMatch = (regex, target) => { calls++; return regex.IsMatch(target); };

        CasbinEnforcementService.MatchObjectPattern("hot.*.path_once", "hot.x.path_once").ShouldBeTrue();
        CasbinEnforcementService.MatchObjectPattern("hot.*.path_once", "hot.y.path_once").ShouldBeTrue();

        calls.ShouldBe(2);
    }
}

[CollectionDefinition("CasbinObjectPattern", DisableParallelization = true)]
public sealed class CasbinObjectPatternCollection;
