namespace Autheris.Domain.Audit;

/// <summary>
/// AU-02: Manifest representing an epoch snapshot of the audit chain tail for external anchor signing.
/// </summary>
public sealed record AnchorManifest
{
    public int Epoch { get; init; }
    public long TailSeq { get; init; }
    public required string TailHash { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public required string TenantId { get; init; }

    /// <summary>
    /// Returns the canonical representation of the manifest used for signing and verification.
    /// </summary>
    public string ToCanonicalString() =>
        $"anchor-v2|{Epoch}|{TailSeq}|{TailHash}|{TimestampUtc.ToUniversalTime():O}|{TenantId}";
}
