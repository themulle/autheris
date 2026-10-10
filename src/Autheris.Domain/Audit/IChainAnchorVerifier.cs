namespace Autheris.Domain.Audit;

/// <summary>
/// AU-02: Verifies asymmetric signatures on anchor manifests using a public key.
/// </summary>
public interface IChainAnchorVerifier
{
    Task<bool> VerifyAnchorAsync(SignedAnchorManifest signedManifest, CancellationToken ct = default);
}
