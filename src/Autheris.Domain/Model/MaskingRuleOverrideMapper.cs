namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// Maps between the persisted column mask override (<c>column_mask_overrides</c>: function code, params, key reference) and the
/// catalog <see cref="MaskingRule"/> view that the enforcement code consumes. The mapping is the inverse of the one the data
/// classification migration applied to the legacy masking rows, so a rule survives a read-modify-write cycle unchanged.
/// A key reference is a handle only; key material is never stored here.
/// </summary>
public static class MaskingRuleOverrideMapper
{
    public const string DefaultConstant = "***";

    /// <summary>Builds the rule view of a persisted override. Malformed params fail closed (the caller marks the table unavailable).</summary>
    public static MaskingRule ToRule(string functionCode, string? paramsJson, string? keyRef, Guid tableColumnId)
    {
        if (!ClassificationMaskFunctions.TryParse(functionCode, out var function))
        {
            throw new FormatException("Corrupted column mask override: unknown mask function.");
        }

        using var doc = ParseParams(paramsJson);
        var root = doc?.RootElement;
        var rule = new MaskingRule { TableColumnId = tableColumnId };
        var built = function switch
        {
            MaskFunction.Nullify => rule with { RuleType = GetBool(root, "deny") ? "DENY" : "NULLIFY" },
            MaskFunction.Hash => rule with { RuleType = "HMAC_SHA256", HmacKeyId = keyRef },
            MaskFunction.Tokenize => rule with { RuleType = "TOKENIZE", PatternOrFormat = GetString(root, "domain"), HmacKeyId = keyRef },
            MaskFunction.Partial => rule with
            {
                RuleType = "PARTIAL_MASK",
                PatternOrFormat = GetString(root, "pattern_or_format"),
                KeepPrefix = GetInt(root, "keep_first") ?? rule.KeepPrefix,
                KeepSuffix = GetInt(root, "keep_last") ?? rule.KeepSuffix,
                MaskChar = GetString(root, "mask_char") is { Length: 1 } mc ? mc[0] : rule.MaskChar
            },
            MaskFunction.GeoJitter => rule with { RuleType = "GEO_JITTER", Decimals = GetInt(root, "decimals") ?? rule.Decimals },
            _ => rule with { RuleType = "REDACT", Replacement = GetString(root, "constant") ?? DefaultConstant }
        };

        // The declared catalog rule type survives the round trip when it maps to the stored function (aliases like MASK_EMAIL or FULL).
        return GetString(root, "rule_type") is { Length: > 0 } declared
            && FromRule(built with { RuleType = declared }).FunctionCode == functionCode
            ? built with { RuleType = declared }
            : built;
    }

    /// <summary>Builds the persisted override parts of a catalog rule. An unknown rule type maps to a type-safe constant redaction.</summary>
    public static (string FunctionCode, string? ParamsJson, string? KeyRef) FromRule(MaskingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var (function, parameters, keyRef) = FromRuleCore(rule);
        return (function, string.IsNullOrWhiteSpace(rule.RuleType) ? parameters : WithRuleType(parameters, rule.RuleType.Trim()), keyRef);
    }

    /// <summary>
    /// Issue #1042: true when both rules persist to the same enforced override, ignoring the declared rule type: the same mask
    /// function, parameters and key reference. REDACT and TYPE_SAFE_REDACT count as one function because both render the same
    /// constant redaction. Fields the override does not persist (for example a geo jitter radius) are not compared here.
    /// </summary>
    public static bool PersistsToSameMask(MaskingRule left, MaskingRule right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (leftFunction, leftParams, leftKey) = FromRuleCore(left);
        var (rightFunction, rightParams, rightKey) = FromRuleCore(right);
        return string.Equals(CanonicalFunction(leftFunction), CanonicalFunction(rightFunction), StringComparison.Ordinal)
            && string.Equals(leftParams, rightParams, StringComparison.Ordinal)
            && string.Equals(leftKey ?? string.Empty, rightKey ?? string.Empty, StringComparison.Ordinal);
    }

    private static readonly string RedactCode = ClassificationMaskFunctions.ToCode(MaskFunction.Redact);
    private static readonly string TypeSafeRedactCode = ClassificationMaskFunctions.ToCode(MaskFunction.TypeSafeRedact);

    private static string CanonicalFunction(string functionCode) =>
        string.Equals(functionCode, RedactCode, StringComparison.Ordinal) ? TypeSafeRedactCode : functionCode;

    private static string WithRuleType(string? paramsJson, string ruleType)
    {
        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            using var doc = JsonDocument.Parse(paramsJson);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                bag[p.Name] = p.Value.Clone();
            }
        }

        bag["rule_type"] = JsonSerializer.SerializeToElement(ruleType);
        return JsonSerializer.Serialize(bag);
    }

    private static (string FunctionCode, string? ParamsJson, string? KeyRef) FromRuleCore(MaskingRule rule)
    {
        return (rule.RuleType ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "NULLIFY" => (ClassificationMaskFunctions.ToCode(MaskFunction.Nullify), null, null),
            // A catalog DENY rule has no mask function of its own: it persists as NULLIFY flagged deny, so the column stays denied.
            "DENY" => (ClassificationMaskFunctions.ToCode(MaskFunction.Nullify), "{\"deny\":true}", null),
            "HMAC" or "HMAC_SHA256" or "HASH" => (ClassificationMaskFunctions.ToCode(MaskFunction.Hash), null, rule.HmacKeyId),
            "MASK_EMAIL" => Redact("***@***", MaskFunction.Redact),
            "MASK_IBAN" => Redact("****", MaskFunction.Redact),
            "PARTIAL_MASK" => (ClassificationMaskFunctions.ToCode(MaskFunction.Partial), PartialParams(rule), null),
            "GEO_JITTER" => (ClassificationMaskFunctions.ToCode(MaskFunction.GeoJitter),
                $"{{\"decimals\":{(rule.Decimals ?? 2).ToString(CultureInfo.InvariantCulture)}}}", null),
            "TOKENIZE" => (ClassificationMaskFunctions.ToCode(MaskFunction.Tokenize),
                JsonSerializer.Serialize(new { domain = rule.PatternOrFormat ?? "default" }), rule.HmacKeyId),
            _ => Redact(rule.Replacement ?? DefaultConstant, MaskFunction.TypeSafeRedact)
        };
    }

    private static (string, string?, string?) Redact(string constant, MaskFunction function) =>
        (ClassificationMaskFunctions.ToCode(function), JsonSerializer.Serialize(new { constant }), null);

    private static string PartialParams(MaskingRule rule) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["keep_first"] = rule.KeepPrefix ?? 1,
        ["keep_last"] = rule.KeepSuffix ?? 0,
        ["mask_char"] = (rule.MaskChar ?? '*').ToString(),
        ["pattern_or_format"] = rule.PatternOrFormat
    }.Where(static kv => kv.Value is not null).ToDictionary(static kv => kv.Key, static kv => kv.Value));

    private static JsonDocument? ParseParams(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return null;
        }

        var doc = JsonDocument.Parse(paramsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new FormatException("Corrupted column mask override: params must be a JSON object.");
        }

        return doc;
    }

    private static string? GetString(JsonElement? root, string name) =>
        root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement? root, string name) =>
        root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement? root, string name) =>
        root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}
