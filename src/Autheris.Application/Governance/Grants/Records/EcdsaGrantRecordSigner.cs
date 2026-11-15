namespace Autheris.Application.Governance.Grants.Records;

using System.Security.Cryptography;
using System.Text;

public sealed class EcdsaGrantRecordSigner : IGrantRecordSigner, IDisposable
{
    private readonly ECDsa? _activePrivateKey;
    private readonly Dictionary<string, ECDsa> _verificationKeys;
    private readonly string? _unavailableReason;

    private EcdsaGrantRecordSigner(ECDsa? activePrivateKey, string activeKeyId, Dictionary<string, ECDsa> verificationKeys, string? unavailableReason) =>
        (_activePrivateKey, ActiveKeyId, _verificationKeys, _unavailableReason) = (activePrivateKey, activeKeyId, verificationKeys, unavailableReason);

    public string ActiveKeyId { get; }

    public static EcdsaGrantRecordSigner FromPem(string? activePrivateKeyPem, string? activeKeyId = null, IReadOnlyDictionary<string, string>? previousPublicKeysPem = null)
    {
        var keys = new Dictionary<string, ECDsa>(StringComparer.Ordinal);
        try
        {
            if (previousPublicKeysPem is not null)
                foreach (var (keyId, pem) in previousPublicKeysPem)
                    if (!string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(pem)) keys[keyId.Trim()] = ImportKey(pem, false);
            if (string.IsNullOrWhiteSpace(activePrivateKeyPem))
                return new EcdsaGrantRecordSigner(null, activeKeyId ?? string.Empty, keys, "No grant record signing key configured.");
            var privateKey = ImportKey(activePrivateKeyPem, true);
            var resolvedId = !string.IsNullOrWhiteSpace(activeKeyId) ? (activeKeyId.Trim() is var t && t.Length <= 64 ? t : t[..64])
                : Convert.ToHexStringLower(SHA256.HashData(privateKey.ExportSubjectPublicKeyInfo()))[..16];
            keys[resolvedId] = ImportKey(privateKey.ExportSubjectPublicKeyInfoPem(), false);
            return new EcdsaGrantRecordSigner(privateKey, resolvedId, keys, null);
        }
        catch (InvalidOperationException) { foreach (var k in keys.Values) k.Dispose(); throw; }
        catch (Exception) { foreach (var k in keys.Values) k.Dispose(); return new(null, activeKeyId ?? string.Empty, new(StringComparer.Ordinal), "Invalid grant record ECDSA P-256 signing key."); }
    }

    public static EcdsaGrantRecordSigner CreateEphemeral(string? activeKeyId = null, IReadOnlyDictionary<string, string>? previousPublicKeysPem = null)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return FromPem(ecdsa.ExportPkcs8PrivateKeyPem(), activeKeyId, previousPublicKeysPem);
    }

    public static EcdsaGrantRecordSigner Unavailable(string reason) => new(null, string.Empty, new(StringComparer.Ordinal), reason);

    public static void EnsureSeparateFromAuditKey(string signingKeyMaterial, ReadOnlySpan<byte> auditHmacKeyBytes)
    {
        if (string.IsNullOrWhiteSpace(signingKeyMaterial) || auditHmacKeyBytes.IsEmpty) return;
        var signingUtf8 = Encoding.UTF8.GetBytes(signingKeyMaterial.Trim());
        if (CryptographicOperations.FixedTimeEquals(signingUtf8, auditHmacKeyBytes) ||
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(signingUtf8), SHA256.HashData(auditHmacKeyBytes)))
            throw new InvalidOperationException("Security critical: Grant record signing key must be distinct from the audit HMAC key.");
    }

    public void EnsureSigningKeyAvailable()
    {
        if (_activePrivateKey is null) throw new GrantRecordUnavailableException(_unavailableReason ?? "Grant record signing key is unavailable.");
    }

    public GrantSignatureResult Sign(ReadOnlySpan<byte> data)
    {
        EnsureSigningKeyAvailable();
        try { return new GrantSignatureResult(Convert.ToBase64String(_activePrivateKey!.SignData(data, HashAlgorithmName.SHA256)), ActiveKeyId); }
        catch (CryptographicException ex) { throw new GrantRecordUnavailableException("Grant record signing failed (fail-closed).", ex); }
    }

    public bool Verify(ReadOnlySpan<byte> data, string signatureBase64, string keyId)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(keyId) || !_verificationKeys.TryGetValue(keyId, out var key)) return false;
        try { return key.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256); }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public void Dispose() { _activePrivateKey?.Dispose(); foreach (var k in _verificationKeys.Values) k.Dispose(); _verificationKeys.Clear(); }

    private static ECDsa ImportKey(string pem, bool requirePrivate)
    {
        var ecdsa = ECDsa.Create();
        try { ecdsa.ImportFromPem(pem); if (ecdsa.KeySize != 256) throw new InvalidOperationException($"Grant record signing requires ECDSA P-256 (got {ecdsa.KeySize}-bit key)."); if (requirePrivate) _ = ecdsa.ExportParameters(true); return ecdsa; }
        catch { ecdsa.Dispose(); throw; }
    }
}
