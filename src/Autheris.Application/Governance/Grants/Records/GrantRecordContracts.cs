namespace Autheris.Application.Governance.Grants.Records;

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

public sealed class GrantRecordUnavailableException : InvalidOperationException
{
    public GrantRecordUnavailableException() : base("Grant record signing or persistence is unavailable (fail-closed).") { }
    public GrantRecordUnavailableException(string message) : base(message) { }
    public GrantRecordUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}

public sealed record GrantRecordInput(
    string TenantId, string ScopeJson, string RequestedBy, string DecisionJson, DateTimeOffset DecidedAt,
    string? ObjectId = null, string? GranteeType = null, string? GranteeRef = null, Guid? RequestId = null,
    ConsentGrantSource? GrantSource = null, Guid? ProfileAssignmentId = null,
    DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidUntil = null, GrantRecordType? RecordType = null);

public sealed record GrantSignatureResult(string Signature, string KeyId);
public sealed record GrantChainVerificationResult(bool IsValid, int VerifiedCount, long LastSeq, string LastHash, long? BrokenAtSeq = null, string? FailureReason = null);

public sealed class GrantVerificationKeySetting
{
    /// <summary>Retired verification key identifier.</summary>
    public string KeyId { get; set; } = string.Empty;
    /// <summary>Retired ECDSA P-256 public key PEM.</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}

public sealed class GrantRecordSigningSettings
{
    public const string SectionName = "Gateway:Governance:GrantRecords";
    /// <summary>ECDSA P-256 private key PEM used to sign grant ledger records.</summary>
    public string SigningKey { get; set; } = string.Empty;
    /// <summary>Key Vault or secret-provider reference resolving the ECDSA P-256 signing key PEM.</summary>
    public string SigningKeyVaultRef { get; set; } = string.Empty;
    /// <summary>Optional key identifier recorded in signed grant ledger rows; defaults to public key SHA-256 prefix.</summary>
    public string KeyId { get; set; } = string.Empty;
    /// <summary>Retired ECDSA P-256 public verification keys retained for rotated signature verification.</summary>
    public List<GrantVerificationKeySetting> PreviousPublicKeys { get; init; } = [];
}

public interface IGrantRecordSigner { string ActiveKeyId { get; } void EnsureSigningKeyAvailable(); GrantSignatureResult Sign(ReadOnlySpan<byte> data); bool Verify(ReadOnlySpan<byte> data, string signatureBase64, string keyId); }
public interface IGrantRecordStore { Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default); Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default); Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default); Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default) => throw new NotSupportedException("InsertConsentRowAsync must be implemented by the grant record store."); Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default); }
public interface IGrantWriter { Task<GrantRecord> InsertAndGrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default); Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default); Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default); Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default); Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default); }
public interface IGrantChainVerifier { Task<GrantChainVerificationResult> VerifyTenantChainAsync(string tenantId, CancellationToken ct = default); }

public static class GrantRecordChain
{
    public static readonly string GenesisHash = new('0', 64);

    public static string ComputeRecordHash(GrantRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(record.PrevHash, "|", GrantRecordCanonicalizer.Canonicalize(record)))));
    }

    public static GrantChainVerificationResult VerifyOrderedRecords(IReadOnlyList<GrantRecord> records, IGrantRecordSigner signer)
    {
        ArgumentNullException.ThrowIfNull(records); ArgumentNullException.ThrowIfNull(signer);
        var (expectedSeq, expectedPrev) = (1L, GenesisHash);
        foreach (var r in records)
        {
            var reason = r.Seq != expectedSeq ? "Sequence gap or out-of-order grant record."
                : !string.Equals(r.PrevHash, expectedPrev, StringComparison.Ordinal) ? "Previous hash link mismatch."
                : !string.Equals(r.RecordHash, ComputeRecordHash(r), StringComparison.Ordinal) ? "Record hash mismatch."
                : !signer.Verify(Encoding.UTF8.GetBytes(r.RecordHash), r.Signature, r.KeyId) ? "Invalid grant record signature."
                : null;
            if (reason is not null) return new GrantChainVerificationResult(false, (int)(expectedSeq - 1), expectedSeq - 1, expectedPrev, r.Seq, reason);
            (expectedPrev, expectedSeq) = (r.RecordHash, expectedSeq + 1);
        }
        return new GrantChainVerificationResult(true, records.Count, expectedSeq - 1, expectedPrev);
    }
}

public sealed class GrantChainVerifier(IGrantRecordStore store, IGrantRecordSigner signer) : IGrantChainVerifier
{
    private readonly IGrantRecordStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IGrantRecordSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    public async Task<GrantChainVerificationResult> VerifyTenantChainAsync(string tenantId, CancellationToken ct = default) =>
        GrantRecordChain.VerifyOrderedRecords(await _store.ReadTenantChainAsync(string.IsNullOrWhiteSpace(tenantId) ? throw new ArgumentException("TenantId is required.", nameof(tenantId)) : tenantId, ct).ConfigureAwait(false), _signer);
}
