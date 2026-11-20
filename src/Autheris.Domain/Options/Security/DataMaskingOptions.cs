using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>Deterministic pseudonymization and data masking configuration (<c>Gateway:DataMasking</c>).</summary>
public sealed class DataMaskingOptions
{
    /// <summary>Identifier of the active HMAC key version embedded in pseudonymized values.</summary>
    public string HmacKeyId { get; init; } = "key-2026-q1";

    /// <summary>Key Vault secret reference for the HMAC-SHA256 pseudonymization key.</summary>
    public string HmacSecretKeyVaultRef { get; init; } = "DEV_INSECURE_TEST_KEY_ONLY";

    /// <summary>
    /// RR-L6-02: When true, prevents binding derived HMAC keys as command parameters in WebSQL in-DB expressions
    /// to avoid key exposure in DB server logs (log_statement), plan cache, profilers or SIEM, failing closed to redaction.
    /// </summary>
    public bool PreventInDbHmacKeyExposure { get; init; } = false;
}
