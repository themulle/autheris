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

/// <summary>D-5: TableMetadata.ToString() does not disclose masking configuration (HMAC key ids).</summary>
public sealed class TableMetadataToStringD5Tests
{
    [Fact]
    public void ToString_PrintsOnlyTheIdentifier()
    {
        var meta = new TableMetadata
        {
            Identifier = new Autheris.Domain.Common.TableIdentifier("hr", "dbo", "employees"),
            ColumnMaskingRules = new System.Collections.Generic.Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = "HMAC", HmacKeyId = "key-2026-secret-id" } }
        };

        var text = meta.ToString();

        text.ShouldContain("employees");
        text.ShouldNotContain("key-2026-secret-id");
        text.ShouldNotContain("ColumnMaskingRules");
    }
}
