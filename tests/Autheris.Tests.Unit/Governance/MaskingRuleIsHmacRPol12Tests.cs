namespace Autheris.Tests.Unit.Governance;

using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>R-POL-12: one definition of keyed pseudonymization rules for all read paths (the lakehouse paths missed HASH).</summary>
public sealed class MaskingRuleIsHmacRPol12Tests
{
    [Theory]
    [InlineData("HMAC", true)]
    [InlineData("hmac_sha256", true)]
    [InlineData(" HASH ", true)]
    [InlineData("HMACX", false)]
    [InlineData("REDACT", false)]
    [InlineData("NULLIFY", false)]
    public void IsHmac_ValueTable(string ruleType, bool expected)
    {
        new MaskingRule { RuleType = ruleType }.IsHmac.ShouldBe(expected);
    }
}
