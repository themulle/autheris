using System.Security.Cryptography;
using System.Text;

namespace Autheris.Domain.Audit;

/// <summary>
/// AU-02: Reference implementation of IChainAnchorSigner and IChainAnchorVerifier supporting ECDSA and RSA.
/// </summary>
public sealed class AsymmetricChainAnchorService : IChainAnchorSigner, IChainAnchorVerifier
{
    private readonly string? _privateKeyPem;
    private readonly string _publicKeyPem;
    private readonly string _keyId;
    private readonly string _keyFingerprint;
    private readonly string _algorithm;

    public AsymmetricChainAnchorService(string? privateKeyPem, string? publicKeyPem = null, string keyId = "default-kms-key")
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem) && string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new ArgumentException("A private or public key PEM is required.");
        }

        _privateKeyPem = privateKeyPem;
        _keyId = keyId;

        using var testKey = !string.IsNullOrWhiteSpace(privateKeyPem) ? FromPem(privateKeyPem) : FromPem(publicKeyPem!);
        _publicKeyPem = publicKeyPem ?? (testKey switch
        {
            ECDsa ec => ec.ExportSubjectPublicKeyInfoPem(),
            RSA rsa => rsa.ExportSubjectPublicKeyInfoPem(),
            _ => throw new CryptographicException("Unsupported algorithm.")
        });

        _algorithm = testKey switch
        {
            ECDsa => "ECDSA_P256_SHA_256",
            RSA => "RSASSA_PSS_SHA_256",
            _ => throw new CryptographicException("Unsupported algorithm.")
        };

        var pubBytes = Encoding.UTF8.GetBytes(_publicKeyPem);
        _keyFingerprint = Convert.ToHexString(SHA256.HashData(pubBytes)).ToLowerInvariant();
    }

    private static AsymmetricAlgorithm FromPem(string pem)
    {
        var ec = ECDsa.Create();
        try
        {
            ec.ImportFromPem(pem);
            return ec;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            ec.Dispose();
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa;
        }
    }

    public Task<SignedAnchorManifest> SignAnchorAsync(AnchorManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_privateKeyPem == null)
        {
            throw new InvalidOperationException("Cannot sign anchor: no private key configured (verify-only).");
        }

        using var key = FromPem(_privateKeyPem);
        var canonicalData = Encoding.UTF8.GetBytes(manifest.ToCanonicalString());
        byte[] signatureBytes = key switch
        {
            ECDsa ec => ec.SignData(canonicalData, HashAlgorithmName.SHA256),
            RSA rsa => rsa.SignData(canonicalData, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            _ => throw new CryptographicException("Unsupported algorithm.")
        };

        var signed = new SignedAnchorManifest
        {
            Manifest = manifest,
            SignatureAlgorithm = _algorithm,
            SignatureBase64 = Convert.ToBase64String(signatureBytes),
            KeyId = _keyId,
            KeyFingerprint = _keyFingerprint
        };

        return Task.FromResult(signed);
    }

    public Task<bool> VerifyAnchorAsync(SignedAnchorManifest signedManifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signedManifest);
        try
        {
            using var key = FromPem(_publicKeyPem);
            var canonicalData = Encoding.UTF8.GetBytes(signedManifest.Manifest.ToCanonicalString());
            var signatureBytes = Convert.FromBase64String(signedManifest.SignatureBase64);

            bool isValid = key switch
            {
                ECDsa ec => ec.VerifyData(canonicalData, signatureBytes, HashAlgorithmName.SHA256),
                RSA rsa => rsa.VerifyData(canonicalData, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
                _ => false
            };

            return Task.FromResult(isValid);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}
