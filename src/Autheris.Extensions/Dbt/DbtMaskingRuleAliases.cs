namespace Autheris.Extensions.Dbt;

/// <summary>Maps the accepted spellings of dbt governance masking rule names to the existing canonical rule types.</summary>
internal static class DbtMaskingRuleAliases
{
    /// <summary>Existing rule types that also accept the <c>&lt;type&gt;_mask</c> / <c>mask_&lt;type&gt;</c> spellings (no new rules).</summary>
    private static readonly string[] MaskTypes = ["EMAIL", "IBAN", "PHONE"];

    public static string Normalize(string raw)
    {
        var trimmed = raw.Trim().ToUpperInvariant();
        return trimmed switch
        {
            "GEO_JITTER" or "GEOJITTER" => "GEO_JITTER",
            "PARTIAL_MASK" or "PARTIAL" or "PARTIALMASK" or "MASK_PARTIAL" => "PARTIAL_MASK",
            "TOKENIZE" or "TOKENIZATION" => "TOKENIZE",
            "NULLING" or "NULLIFY" => "NULLIFY",
            "PSEUDONYMIZE" or "PSEUDONYMIZATION" or "HMAC" or "HMAC_SHA256" => "HMAC",
            "REDACT_COMPLETE" or "REDACT" => "REDACT",
            _ => NormalizeMaskType(trimmed)
        };
    }

    private static string NormalizeMaskType(string upper)
    {
        foreach (var type in MaskTypes)
        {
            if (upper == type + "_MASK")
            {
                return "MASK_" + type;
            }
        }

        return upper;
    }
}
