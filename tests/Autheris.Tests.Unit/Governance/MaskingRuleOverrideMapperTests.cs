namespace Autheris.Tests.Unit.Governance;

using System;
using System.Linq;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class MaskingRuleOverrideMapperTests
{
    [Theory]
    [InlineData("NULLIFY")]
    [InlineData("DENY")]
    [InlineData("HMAC_SHA256")]
    [InlineData("TOKENIZE")]
    [InlineData("PARTIAL_MASK")]
    [InlineData("GEO_JITTER")]
    [InlineData("REDACT")]
    [InlineData("MASK_EMAIL")]
    [InlineData("MASK_IBAN")]
    [InlineData("FULL")]
    [InlineData("HMAC")]
    public void Rule_SurvivesPersistAndRead_WithTheSameEnforcementKind(string ruleType)
    {
        var rule = new MaskingRule { RuleType = ruleType, Replacement = ruleType == "REDACT" ? "XX" : null, PatternOrFormat = ruleType is "TOKENIZE" or "PARTIAL_MASK" ? "d" : null, HmacKeyId = "k1" };

        var (function, parameters, keyRef) = MaskingRuleOverrideMapper.FromRule(rule);
        var read = MaskingRuleOverrideMapper.ToRule(function, parameters, keyRef, Guid.NewGuid());

        read.RuleType.ShouldBe(ruleType);
        read.IsHmac.ShouldBe(rule.IsHmac);
        if (rule.IsHmac || ruleType == "TOKENIZE")
        {
            keyRef.ShouldBe("k1");
            read.HmacKeyId.ShouldBe("k1");
        }
        else
        {
            keyRef.ShouldBeNull();
        }

        if (ruleType == "REDACT")
        {
            read.Replacement.ShouldBe("XX");
        }
    }

    [Fact]
    public void LegacyHmacAliasesAndUnknownTypes_FailClosedToTheSameFunctionsAsTheBackfill()
    {
        MaskingRuleOverrideMapper.FromRule(new MaskingRule { RuleType = "HMAC", HmacKeyId = "k" }).FunctionCode.ShouldBe("HASH");
        MaskingRuleOverrideMapper.FromRule(new MaskingRule { RuleType = "MASK_EMAIL" }).FunctionCode.ShouldBe("REDACT");
        MaskingRuleOverrideMapper.FromRule(new MaskingRule { RuleType = "SOMETHING_ELSE" }).FunctionCode.ShouldBe("TYPE_SAFE_REDACT");
    }

    [Fact]
    public void CorruptOverride_FailsClosed()
    {
        Should.Throw<FormatException>(() => MaskingRuleOverrideMapper.ToRule("NOPE", null, null, Guid.NewGuid()));
        Should.Throw<System.Text.Json.JsonException>(() => MaskingRuleOverrideMapper.ToRule("REDACT", "{", null, Guid.NewGuid()));
    }

    [Fact]
    public void PiiClassifiedColumn_HasTheSameSensitivityDecision_AsTheLegacyFlag()
    {
        var column = new TableColumn { ColumnName = "ssn" };
        var meta = new TableMetadata { Columns = [column, new TableColumn { ColumnName = "id" }] }.WithPii("ssn");

        ClassificationMetadata.IsColumnSensitive(meta, "ssn").ShouldBeTrue();
        ClassificationMetadata.IsColumnSensitive(meta, "id").ShouldBeFalse();
        ClassificationMetadata.IsTableSensitive(meta).ShouldBeTrue();
        ConsentColumnSnapshot.FromMetadata(meta).Single(r => r.ColumnName == "ssn").AccessLevel.ShouldBe(Autheris.Domain.Interfaces.ColumnAccessLevel.Mask);
        ConsentColumnSnapshot.FromMetadata(meta).Single(r => r.ColumnName == "id").AccessLevel.ShouldBe(Autheris.Domain.Interfaces.ColumnAccessLevel.Clear);
    }

    [Fact]
    public void UnknownColumn_WithoutClassification_IsNotSensitive_ButAMaskRuleMakesItSensitive()
    {
        var meta = new TableMetadata
        {
            Columns = [new TableColumn { ColumnName = "a" }],
            ColumnMaskingRules = new System.Collections.Generic.Dictionary<string, MaskingRule> { ["a"] = new() { RuleType = "REDACT" } }
        };

        ClassificationMetadata.IsColumnSensitive(meta, "a").ShouldBeTrue();
    }

    [Fact]
    public void IngestionMerge_IsAdditive_NeverRemovesAnExistingClassification()
    {
        var existing = new TableMetadata { Columns = [new TableColumn { ColumnName = "a" }] }.WithPii("a").ColumnClassifications;
        var incomingNone = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<ColumnClassification>>();

        ClassificationMetadata.Merge(existing, incomingNone).ShouldContainKey("a");
        ClassificationMetadata.Merge(existing, existing)["a"].Count.ShouldBe(1);
    }
}
