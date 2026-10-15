using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class DataMaskingOptions
{
    public string HmacKeyId { get; init; } = "key-2026-q1";
    public string HmacSecretKeyVaultRef { get; init; } = "DEV_INSECURE_TEST_KEY_ONLY";
    [Range(1, 168)] public int MaskingCacheTtlHours { get; init; } = 24;

    /// <summary>
    /// RR-L6-02: When true, prevents binding derived HMAC keys as command parameters in WebSQL in-DB expressions
    /// to avoid key exposure in DB server logs (log_statement), plan cache, profilers or SIEM, failing closed to redaction.
    /// </summary>
    public bool PreventInDbHmacKeyExposure { get; init; } = false;
}
