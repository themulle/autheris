namespace Autheris.Tests.Unit.Governance;

using System;
using Autheris.Application.Governance;
using Shouldly;
using Xunit;

/// <summary>GOV-05: a short-form deny line in the global policy file must be rejected instead of silently becoming a non-matching allow.</summary>
[Trait("Category", "Security")]
public sealed class CasbinGlobalPolicyShortFormTests
{
    [Theory]
    [InlineData("p, S-1-5-21-contractors, hr.dbo.salaries, read, deny")]
    [InlineData("p, S-1-5-21-contractors, hr:dbo:salaries, read, deny")]
    [InlineData("p, S-1-5-21-contractors, tenant-a, hr.dbo.salaries, deny")]
    public void GlobalFile_ShortFormOrEffectInActionPosition_IsRejected_GOV05(string line)
    {
        Should.Throw<FormatException>(() =>
            CasbinEnforcementService.ParsePolicyText(line, defaultTenant: null, modelSupportsWildcardTenant: true));
    }

    [Fact]
    public void GlobalFile_LongForm_StillParses_GOV05()
    {
        var (rules, _) = CasbinEnforcementService.ParsePolicyText(
            "p, S-1-5-21-contractors, tenant-a, hr.dbo.salaries, read, true, deny", defaultTenant: null, modelSupportsWildcardTenant: true);

        rules.ContainsKey("tenant-a").ShouldBeTrue();
    }

    [Fact]
    public void TenantFile_ShortForm_StillParses_GOV05()
    {
        var (rules, _) = CasbinEnforcementService.ParsePolicyText(
            "p, S-1-5-21-contractors, hr.dbo.salaries, read, deny", defaultTenant: "tenant-a", modelSupportsWildcardTenant: true);

        rules["tenant-a"].Length.ShouldBe(1);
    }
}
