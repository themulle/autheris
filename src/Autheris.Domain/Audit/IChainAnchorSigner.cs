namespace Autheris.Domain.Audit;

/// <summary>
/// AU-02: Signs anchor manifests using an asymmetric key in a separate KMS / HSM trust domain.
/// </summary>
public interface IChainAnchorSigner
{
    Task<SignedAnchorManifest> SignAnchorAsync(AnchorManifest manifest, CancellationToken ct = default);
}
