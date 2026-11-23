namespace Autheris.Tests.Unit.Catalog;

using System;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1042: after the v1.8.5 → v1.10.x upgrade, migration 0023 stored the legacy masking rules as column mask overrides
/// without their declared rule type, and the catalog upsert later wrote the canonical names back as the declared type. An
/// unchanged dbt governance file must not count as a relaxation against these rows; a genuine weakening still must.
/// The stored rules are built with <see cref="MaskingRuleOverrideMapper.ToRule"/> from the persisted row shapes, as the repository reads them.
/// </summary>
[Trait("Category", "Security")]
public sealed class MaskingRelaxationUpgradeTests
{
    private static MaskingRule Stored(string function, string? paramsJson, string? keyRef = null) =>
        MaskingRuleOverrideMapper.ToRule(function, paramsJson, keyRef, Guid.NewGuid());

    // The rules the dbt governance import builds (DbtMetadataIngestionService: normalized type, REDACT gets "[REDACTED]").
    private static MaskingRule Incoming(string ruleType) =>
        new() { RuleType = ruleType, Replacement = ruleType == "REDACT" ? "[REDACTED]" : null };

    public static TheoryData<string, string?, string> UnchangedRulesAgainstUpgradedRows => new()
    {
        // system:migration-0023 rows (no rule_type)
        { "HASH", null, "HMAC" },
        { "REDACT", "{\"constant\":\"***@***\"}", "MASK_EMAIL" },
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***\"}", "MASK_PHONE" },
        // system:catalog-upsert rows (canonical name written back as rule_type)
        { "HASH", "{\"rule_type\":\"HMAC_SHA256\"}", "HMAC" },
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***@***\",\"rule_type\":\"REDACT\"}", "MASK_EMAIL" },
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***\",\"rule_type\":\"REDACT\"}", "MASK_PHONE" },
        // rows the fixed import writes (declared type kept)
        { "REDACT", "{\"constant\":\"***@***\",\"rule_type\":\"MASK_EMAIL\"}", "MASK_EMAIL" },
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***\",\"rule_type\":\"MASK_PHONE\"}", "MASK_PHONE" },
        { "HASH", "{\"rule_type\":\"HMAC\"}", "HMAC" },
    };

    [Theory]
    [MemberData(nameof(UnchangedRulesAgainstUpgradedRows))]
    public void UnchangedGovernanceRule_AgainstUpgradedRow_IsNotARelaxation(string function, string? paramsJson, string incomingType)
    {
        var stored = Stored(function, paramsJson);

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, Incoming(incomingType)).ShouldBeFalse(
            $"{incomingType} persists to the same override as the stored {function} {paramsJson} row.");
    }

    [Theory]
    [InlineData("HMAC", "HMAC_SHA256")]
    [InlineData("HMAC_SHA256", "HMAC")]
    [InlineData("HASH", "HMAC")]
    [InlineData("HMAC", "HASH")]
    public void HmacAliases_WithTheSameKey_AreNotARelaxation(string storedType, string incomingType)
    {
        var stored = new MaskingRule { RuleType = storedType, HmacKeyId = "k1" };
        var incoming = new MaskingRule { RuleType = incomingType, HmacKeyId = "k1" };

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeFalse();
    }

    public static TheoryData<string, string?, string?, string> GenuineWeakenings => new()
    {
        // hash → constant e-mail redaction reveals the column shape differently and drops the join key: weaker tier
        { "HASH", null, null, "MASK_EMAIL" },
        // full redaction with the default constant → e-mail constant: a different (not stricter) mask
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***\"}", null, "MASK_EMAIL" },
        // nullify → anything that renders a value
        { "NULLIFY", null, null, "MASK_PHONE" },
        { "NULLIFY", null, null, "HMAC" },
        // keyed hash → hash with another (default) key
        { "HASH", null, "tenant-key", "HMAC" },
        // redaction → hash (a join key is revealed)
        { "TYPE_SAFE_REDACT", "{\"constant\":\"***\",\"rule_type\":\"REDACT\"}", null, "HMAC" },
    };

    [Theory]
    [MemberData(nameof(GenuineWeakenings))]
    public void GenuineWeakening_AgainstUpgradedRow_IsStillARelaxation(string function, string? paramsJson, string? keyRef, string incomingType)
    {
        var stored = Stored(function, paramsJson, keyRef);

        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, Incoming(incomingType)).ShouldBeTrue(
            $"{incomingType} weakens the stored {function} {paramsJson} rule.");
    }

    [Fact]
    public void SamePersistedGeoJitter_WithASmallerRadius_IsStillARelaxation()
    {
        // The override persists only the decimals; the radius the rule carries is compared on top of the persisted form.
        var stored = new MaskingRule { RuleType = "GEO_JITTER", Decimals = 2, Mode = "noise", RadiusMeters = 1000 };
        var incoming = new MaskingRule { RuleType = "GEO_JITTER", Decimals = 2, Mode = "noise", RadiusMeters = 100 };

        MaskingRuleOverrideMapper.PersistsToSameMask(stored, incoming).ShouldBeTrue();
        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue();
    }

    [Fact]
    public void SamePersistedPartialMask_ThatRevealsTheLength_IsStillARelaxation()
    {
        var stored = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 1, KeepSuffix = 0, FixedLength = true };
        var incoming = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 1, KeepSuffix = 0, FixedLength = false };

        MaskingRuleOverrideMapper.PersistsToSameMask(stored, incoming).ShouldBeTrue();
        CatalogGovernanceRatchet.IsMaskingRelaxation(stored, incoming).ShouldBeTrue();
    }

    [Fact]
    public void Deny_AndPlainNullify_DoNotPersistToTheSameMask()
    {
        MaskingRuleOverrideMapper.PersistsToSameMask(
            Stored("NULLIFY", "{\"deny\":true}"),
            new MaskingRule { RuleType = "NULLIFY" }).ShouldBeFalse();
    }

    [Fact]
    public void RedactAndTypeSafeRedact_WithTheSameConstant_PersistToTheSameMask()
    {
        MaskingRuleOverrideMapper.PersistsToSameMask(
            Stored("REDACT", "{\"constant\":\"***@***\"}"),
            Stored("TYPE_SAFE_REDACT", "{\"constant\":\"***@***\"}")).ShouldBeTrue();
        MaskingRuleOverrideMapper.PersistsToSameMask(
            Stored("REDACT", "{\"constant\":\"***@***\"}"),
            Stored("TYPE_SAFE_REDACT", "{\"constant\":\"***\"}")).ShouldBeFalse();
    }
}
