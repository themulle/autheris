namespace Autheris.Domain.Audit;

/// <summary>
/// AU-02: Cryptographically signed anchor manifest containing signature and KMS key metadata.
/// </summary>
public sealed record SignedAnchorManifest
{
    public required AnchorManifest Manifest { get; init; }
    public required string SignatureAlgorithm { get; init; } // e.g., "RSASSA_PSS_SHA_256" or "ECDSA_P256_SHA_256"
    public required string SignatureBase64 { get; init; }
    public required string KeyId { get; init; }
    public required string KeyFingerprint { get; init; }
}
