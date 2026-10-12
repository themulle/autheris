namespace Autheris.Domain.Model;

using System;
using System.Text.Json.Serialization;

public sealed record MaskingRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TableColumnId { get; init; }

    [JsonPropertyName("rule_type")]
    public string RuleType { get; init; } = "REDACT"; // REDACT, NULLIFY, HMAC, MASK_EMAIL, MASK_PHONE, GEO_JITTER, PARTIAL_MASK, TOKENIZE

    [JsonPropertyName("pattern_or_format")]
    public string? PatternOrFormat { get; init; }

    [JsonPropertyName("replacement")]
    public string? Replacement { get; init; }

    [JsonPropertyName("hmac_key_id")]
    public string? HmacKeyId { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; } // GEO_JITTER: "round" (Standard) oder "noise"

    private readonly int? _decimals = 2;

    [JsonPropertyName("decimals")]
    public int? Decimals
    {
        get => _decimals;
        init => _decimals = value.HasValue ? Math.Clamp(value.Value, 0, 6) : 2;
    }

    [JsonPropertyName("radius_meters")]
    public double? RadiusMeters { get; init; } = 500.0; // GEO_JITTER noise: Maximalradius in Metern

    [JsonPropertyName("keep_prefix")]
    public int? KeepPrefix { get; init; } = 1; // PARTIAL_MASK: Sichtbare Anfangszeichen

    [JsonPropertyName("keep_suffix")]
    public int? KeepSuffix { get; init; } = 0; // PARTIAL_MASK: Sichtbare Endzeichen

    [JsonPropertyName("mask_char")]
    public char? MaskChar { get; init; } = '*'; // PARTIAL_MASK: Maskierungszeichen

    [JsonPropertyName("fixed_length")]
    public bool? FixedLength { get; init; } = false; // PARTIAL_MASK: Verbirgt Originallänge

    [JsonPropertyName("token_domain")]
    public string? TokenDomain { get; init; } // TOKENIZE: z. B. "customer_id", "iban"

    /// <summary>R-POL-12: the one definition of a keyed pseudonymization rule (HMAC, HMAC_SHA256 and the HASH alias).</summary>
    [JsonIgnore]
    public bool IsHmac => (RuleType ?? string.Empty).Trim().ToUpperInvariant() is "HMAC" or "HMAC_SHA256" or "HASH";

    /// <summary>
    /// SEC H-13 / SEC D-3: Creates a tenant-scoped copy of an HMAC masking rule, keyed as {baseKeyId}|tenant:{tenant}.
    /// Idempotent: a rule that is already scoped to the requested tenant is returned unchanged.
    /// Rejects rules that are already scoped to a DIFFERENT tenant (prevents cross-tenant correlation).
    /// </summary>
    public static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        var expectedSuffix = $"|tenant:{tenant}";
        if (rule.HmacKeyId != null)
        {
            if (rule.HmacKeyId.EndsWith(expectedSuffix, StringComparison.Ordinal))
            {
                return rule;
            }

            if (rule.HmacKeyId.Contains("|tenant:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The masking rule is already bound to another tenant ('{rule.HmacKeyId}'). Cross-tenant use for tenant '{tenant}' is not allowed.");
            }
        }

        var baseKeyId = !string.IsNullOrWhiteSpace(rule.HmacKeyId) ? rule.HmacKeyId : (defaultKeyId ?? "default");
        return rule with
        {
            RuleType = "HMAC_SHA256",
            HmacKeyId = $"{baseKeyId}{expectedSuffix}"
        };
    }
}
