namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class MaskingRelaxationTests
{
    [Fact]
    public void IsMaskingRelaxation_Redact_ToWeakerRule_IsRelaxation()
    {
        var stored = new MaskingRule { RuleType = "REDACT" };
        var weakerRules = new[] { "MASK_EMAIL", "GEO_JITTER", "PARTIAL_MASK", "HMAC", "TOKENIZE" };

        foreach (var rule in weakerRules)
        {
            var incoming = new MaskingRule { RuleType = rule };
            CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue($"Changing REDACT to {rule} should be detected as relaxation.");
        }
    }

    [Fact]
    public void IsMaskingRelaxation_WeakerRule_ToRedact_IsNotRelaxation()
    {
        var incoming = new MaskingRule { RuleType = "REDACT" };
        var weakerRules = new[] { "MASK_EMAIL", "GEO_JITTER", "PARTIAL_MASK", "HMAC", "TOKENIZE" };

        foreach (var rule in weakerRules)
        {
            var stored = new MaskingRule { RuleType = rule };
            CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeFalse($"Tightening {rule} to REDACT should not be relaxation.");
        }
    }

    [Fact]
    public void IsMaskingRelaxation_GeoJitter_RadiusDecrease_IsRelaxation()
    {
        var stored = new MaskingRule { RuleType = "GEO_JITTER", RadiusMeters = 1000 };
        var incoming = new MaskingRule { RuleType = "GEO_JITTER", RadiusMeters = 500 };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue("Decreasing jitter radius reveals more precision.");
    }

    [Fact]
    public void IsMaskingRelaxation_GeoJitter_RadiusIncrease_IsNotRelaxation()
    {
        var stored = new MaskingRule { RuleType = "GEO_JITTER", RadiusMeters = 500 };
        var incoming = new MaskingRule { RuleType = "GEO_JITTER", RadiusMeters = 1000 };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeFalse("Increasing jitter radius tightens privacy.");
    }

    [Fact]
    public void IsMaskingRelaxation_PartialMask_MoreRevealedChars_IsRelaxation()
    {
        var stored = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2 };
        var incoming = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 4, KeepSuffix = 4 };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue("Revealing more characters is a relaxation.");
    }

    [Fact]
    public void IsMaskingRelaxation_PartialMask_FewerRevealedChars_IsNotRelaxation()
    {
        var stored = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 4, KeepSuffix = 4 };
        var incoming = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2 };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeFalse("Revealing fewer characters tightens privacy.");
    }

    [Fact]
    public void IsMaskingRelaxation_IdenticalRules_IsNotRelaxation()
    {
        var rule1 = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2, MaskChar = '*' };
        var rule2 = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2, MaskChar = '*' };

        CatalogGovernanceRatchet.IsMaskingRelaxation(rule1, rule2).ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("HMAC")]
    [InlineData("REDACT")]
    [InlineData("NULLIFY")]
    [InlineData("MASK_EMAIL")]
    public void IsMaskingRelaxation_Deny_ToAnyMask_IsRelaxation(string incomingType)
    {
        var stored = new MaskingRule { RuleType = "DENY" };
        var incoming = new MaskingRule { RuleType = incomingType };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue($"A denied column must not become readable through {incomingType}.");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("HMAC")]
    [InlineData("REDACT")]
    [InlineData("NULLIFY")]
    [InlineData("MASK_EMAIL")]
    public void IsMaskingRelaxation_AnyMask_ToDeny_IsNotRelaxation(string storedType)
    {
        var stored = new MaskingRule { RuleType = storedType };
        var incoming = new MaskingRule { RuleType = "DENY" };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MaskingRuleStrength_Deny_IsStrictestTier()
    {
        var deny = CatalogGovernanceRatchet.MaskingRuleStrength(new MaskingRule { RuleType = " deny " });

        foreach (var other in new[] { "NULLIFY", "REDACT", "HMAC", "TOKENIZE", "MASK_EMAIL" })
        {
            deny.ShouldBeGreaterThan(CatalogGovernanceRatchet.MaskingRuleStrength(new MaskingRule { RuleType = other }));
        }
    }
}
