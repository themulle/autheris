namespace Autheris.Application.Governance.Grants.Records;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

/// <summary>
/// Exports cryptographically signed grant record chains with correlated data-access audit log entries
/// and verifies grant record chains or submitted export bundles.
/// </summary>
public interface IGrantRecordExportService
{
    Task<GrantRecordExportBundle?> ExportForGrantAsync(TenantId tenantId, Guid grantId, CancellationToken ct = default);
    Task<GrantChainVerificationResult?> VerifyAsync(TenantId tenantId, GrantRecordVerifyRequest request, CancellationToken ct = default);
    Task<GrantRecord?> GetRecordBySeqAsync(TenantId tenantId, long seq, CancellationToken ct = default) => Task.FromResult<GrantRecord?>(null);
    Task<GrantProofEvaluationResult> EvaluateProofAsync(
        TenantId tenantId,
        string? granteeRef = null,
        string? tableRef = null,
        string? scopeHash = null,
        DateTimeOffset? atTime = null,
        CancellationToken ct = default) => Task.FromResult(new GrantProofEvaluationResult(false, tenantId.Value, null, null, 0, "Not implemented.", Array.Empty<GrantRecord>()));
    IReadOnlyList<GrantPublicKeyJwk> GetPublicKeys() => Array.Empty<GrantPublicKeyJwk>();
}

public sealed record GrantProofEvaluationResult(
    bool Verified,
    string TenantId,
    long? HeadSeq,
    string? HeadHash,
    int CheckedCount,
    string? FailureReason,
    IReadOnlyList<GrantRecord> EvaluatedRecords);

public sealed record GrantRecordAuditEntry(
    Guid AuditId,
    DateTimeOffset OccurredAt,
    string EventType,
    string ActorSid,
    string TargetTable,
    string? TargetColumn,
    string Decision,
    string TraceId,
    string DetailsJson);

public sealed record GrantRecordExportBundle(
    string TenantId,
    Guid GrantId,
    bool ChainVerified,
    GrantChainVerificationResult Verification,
    IReadOnlyList<string> KeyIds,
    IReadOnlyList<GrantRecord> Records,
    IReadOnlyList<GrantRecordAuditEntry> CorrelatedAuditEntries);

public sealed record GrantRecordVerifyRequest(
    Guid? GrantId = null,
    string? TenantId = null,
    IReadOnlyList<GrantRecord>? Records = null,
    long? FromSeq = null,
    long? ToSeq = null);

public sealed class GrantRecordExportService(
    IGrantRecordStore store,
    IGrantRecordSigner signer,
    IGrantChainVerifier chainVerifier,
    IAuditLogRepository auditRepository) : IGrantRecordExportService
{
    private readonly IGrantRecordStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IGrantRecordSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    private readonly IGrantChainVerifier _chainVerifier = chainVerifier ?? throw new ArgumentNullException(nameof(chainVerifier));
    private readonly IAuditLogRepository _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));

    public async Task<GrantRecordExportBundle?> ExportForGrantAsync(TenantId tenantId, Guid grantId, CancellationToken ct = default)
    {
        if (grantId == Guid.Empty)
        {
            return null;
        }

        var records = await _store.ReadConsentChainAsync(tenantId.Value, grantId, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return null;
        }

        var verification = VerifySubsetRecords(records, tenantId.Value, grantId);
        var keyIds = records
            .Select(r => r.KeyId)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var auditEntries = await _auditRepository
            .QueryAuditLogsAsync(limit: 1000, tenantId: tenantId, ct: ct)
            .ConfigureAwait(false);

        var correlated = auditEntries
            .Where(e => e.TenantId == tenantId && ContainsGrantReference(e.EffectiveDetailsJson, grantId))
            .Select(e => new GrantRecordAuditEntry(
                e.Id,
                e.OccurredAt,
                e.EventType,
                e.ActorSid.Value,
                e.TargetTable,
                e.TargetColumn,
                e.Decision,
                e.TraceId,
                e.EffectiveDetailsJson))
            .ToArray();

        return new GrantRecordExportBundle(
            tenantId.Value,
            grantId,
            verification.IsValid,
            verification,
            keyIds,
            records,
            correlated);
    }

    public async Task<GrantChainVerificationResult?> VerifyAsync(TenantId tenantId, GrantRecordVerifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Records is { Count: > 0 } submitted)
        {
            return request.GrantId is Guid targetGrantId
                ? VerifySubsetRecords(submitted, tenantId.Value, targetGrantId)
                : VerifyFullTenantBundle(submitted, tenantId.Value);
        }

        if (request.GrantId is Guid storedGrantId)
        {
            if (storedGrantId == Guid.Empty)
            {
                return null;
            }

            var stored = await _store.ReadConsentChainAsync(tenantId.Value, storedGrantId, ct).ConfigureAwait(false);
            if (stored.Count == 0)
            {
                return null;
            }

            return VerifySubsetRecords(stored, tenantId.Value, storedGrantId);
        }

        if (request.FromSeq.HasValue || request.ToSeq.HasValue)
        {
            var fromSeq = Math.Max(1L, request.FromSeq ?? 1L);
            if (request.ToSeq.HasValue && request.ToSeq.Value < fromSeq)
            {
                return new GrantChainVerificationResult(false, 0, 0, GrantRecordChain.GenesisHash, fromSeq, "Invalid sequence range: toSeq must be greater than or equal to fromSeq.");
            }

            var slice = await _store.ReadRangeAsync(tenantId.Value, fromSeq, request.ToSeq, ct).ConfigureAwait(false);
            if (slice.Count == 0)
            {
                return null;
            }

            var anchorPrevHash = GrantRecordChain.GenesisHash;
            if (fromSeq > 1)
            {
                var predecessor = await _store.ReadBySeqAsync(tenantId.Value, fromSeq - 1, ct).ConfigureAwait(false);
                if (predecessor is null)
                {
                    return new GrantChainVerificationResult(false, 0, fromSeq - 1, GrantRecordChain.GenesisHash, fromSeq, "Missing predecessor grant record for range anchor.");
                }

                anchorPrevHash = predecessor.RecordHash;
            }

            return VerifyContiguousRange(slice, fromSeq, anchorPrevHash);
        }

        return await _chainVerifier.VerifyTenantChainAsync(tenantId.Value, ct).ConfigureAwait(false);
    }

    public Task<GrantRecord?> GetRecordBySeqAsync(TenantId tenantId, long seq, CancellationToken ct = default) =>
        seq <= 0 ? Task.FromResult<GrantRecord?>(null) : _store.ReadBySeqAsync(tenantId.Value, seq, ct);

    public IReadOnlyList<GrantPublicKeyJwk> GetPublicKeys() => _signer.GetPublicKeys();

    public async Task<GrantProofEvaluationResult> EvaluateProofAsync(
        TenantId tenantId,
        string? granteeRef = null,
        string? tableRef = null,
        string? scopeHash = null,
        DateTimeOffset? atTime = null,
        CancellationToken ct = default)
    {
        var chain = await _store.ReadTenantChainAsync(tenantId.Value, ct).ConfigureAwait(false);
        var verification = GrantRecordChain.VerifyOrderedRecords(chain, _signer);

        var latestPerConsent = new Dictionary<Guid, GrantRecord>();
        var matchingRecords = new List<GrantRecord>();
        foreach (var r in chain)
        {
            if (r.ConsentId is { } cid)
            {
                latestPerConsent[cid] = r;
            }

            if (!string.IsNullOrWhiteSpace(granteeRef) && !string.Equals(r.GranteeRef, granteeRef.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tableRef) && !string.Equals(r.ObjectId, tableRef.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(scopeHash))
            {
                var computedScopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.ScopeJson)));
                if (!string.Equals(computedScopeHash, scopeHash.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (atTime is { } checkAt)
            {
                var inWindow = (!r.ValidFrom.HasValue || r.ValidFrom.Value <= checkAt)
                    && (!r.ValidUntil.HasValue || checkAt < r.ValidUntil.Value);
                if (!inWindow)
                {
                    continue;
                }
            }

            matchingRecords.Add(r);
        }

        var head = chain.Count > 0 ? chain[^1] : null;
        if (!verification.IsValid)
        {
            return new GrantProofEvaluationResult(false, tenantId.Value, verification.LastSeq, verification.LastHash, matchingRecords.Count, verification.FailureReason, matchingRecords);
        }

        var activeMatches = matchingRecords.Where(r =>
            (r.RecordType is GrantRecordType.Issued or GrantRecordType.Renewed)
            && (!r.ConsentId.HasValue || (latestPerConsent.TryGetValue(r.ConsentId.Value, out var latest) && (latest.RecordType is GrantRecordType.Issued or GrantRecordType.Renewed)))).ToList();

        var verified = activeMatches.Count > 0 && (string.IsNullOrWhiteSpace(granteeRef) && string.IsNullOrWhiteSpace(tableRef) && !atTime.HasValue ? chain.Count > 0 : true);
        if (string.IsNullOrWhiteSpace(granteeRef) && string.IsNullOrWhiteSpace(tableRef) && string.IsNullOrWhiteSpace(scopeHash) && !atTime.HasValue)
        {
            verified = verification.IsValid;
            return new GrantProofEvaluationResult(verified, tenantId.Value, head?.Seq ?? 0, head?.RecordHash ?? GrantRecordChain.GenesisHash, chain.Count, null, chain);
        }

        return new GrantProofEvaluationResult(
            verified,
            tenantId.Value,
            head?.Seq ?? 0,
            head?.RecordHash ?? GrantRecordChain.GenesisHash,
            activeMatches.Count,
            activeMatches.Count > 0 ? null : "No active verified grant matched the proof criteria.",
            activeMatches);
    }

    private GrantChainVerificationResult VerifyContiguousRange(IReadOnlyList<GrantRecord> records, long expectedStartSeq, string expectedPrevHash)
    {
        var expectedSeq = expectedStartSeq;
        var expectedPrev = expectedPrevHash;
        var verifiedCount = 0;

        foreach (var r in records)
        {
            var reason = r.Seq != expectedSeq ? "Sequence gap or out-of-order grant record."
                : !string.Equals(r.PrevHash, expectedPrev, StringComparison.Ordinal) ? "Previous hash link mismatch."
                : !string.Equals(r.RecordHash, GrantRecordChain.ComputeRecordHash(r), StringComparison.Ordinal) ? "Record hash mismatch."
                : string.IsNullOrWhiteSpace(r.KeyId) || string.IsNullOrWhiteSpace(r.Signature) || !_signer.Verify(Encoding.UTF8.GetBytes(r.RecordHash), r.Signature, r.KeyId) ? "Invalid grant record signature."
                : null;
            if (reason is not null)
            {
                return new GrantChainVerificationResult(false, verifiedCount, expectedSeq - 1, expectedPrev, r.Seq, reason);
            }

            verifiedCount++;
            expectedPrev = r.RecordHash;
            expectedSeq++;
        }

        return new GrantChainVerificationResult(true, verifiedCount, expectedSeq - 1, expectedPrev);
    }

    private GrantChainVerificationResult VerifyFullTenantBundle(IReadOnlyList<GrantRecord> records, string expectedTenantId)
    {
        foreach (var r in records)
        {
            if (!string.Equals(r.TenantId, expectedTenantId, StringComparison.Ordinal))
            {
                return new GrantChainVerificationResult(false, 0, 0, GrantRecordChain.GenesisHash, r.Seq, "Grant record tenant mismatch.");
            }
        }

        return GrantRecordChain.VerifyOrderedRecords(records, _signer);
    }

    private GrantChainVerificationResult VerifySubsetRecords(IReadOnlyList<GrantRecord> records, string expectedTenantId, Guid expectedGrantId)
    {
        long? prevSeq = null;
        string? prevHash = null;
        var verifiedCount = 0;

        foreach (var r in records)
        {
            string? reason = null;
            if (!string.Equals(r.TenantId, expectedTenantId, StringComparison.Ordinal))
            {
                reason = "Grant record tenant mismatch.";
            }
            else if (r.ConsentId != expectedGrantId)
            {
                reason = "Grant record consentId mismatch.";
            }
            else if (r.Seq < 1 || (prevSeq.HasValue && r.Seq <= prevSeq.Value))
            {
                reason = "Sequence gap or out-of-order grant record.";
            }
            else if (r.Seq == 1 && !string.Equals(r.PrevHash, GrantRecordChain.GenesisHash, StringComparison.Ordinal))
            {
                reason = "Previous hash link mismatch.";
            }
            else if (prevSeq.HasValue && r.Seq == prevSeq.Value + 1 && !string.Equals(r.PrevHash, prevHash, StringComparison.Ordinal))
            {
                reason = "Previous hash link mismatch.";
            }
            else if (!string.Equals(r.RecordHash, GrantRecordChain.ComputeRecordHash(r), StringComparison.Ordinal))
            {
                reason = "Record hash mismatch.";
            }
            else if (string.IsNullOrWhiteSpace(r.KeyId)
                     || string.IsNullOrWhiteSpace(r.Signature)
                     || !_signer.Verify(Encoding.UTF8.GetBytes(r.RecordHash), r.Signature, r.KeyId))
            {
                reason = "Invalid grant record signature.";
            }

            if (reason is not null)
            {
                return new GrantChainVerificationResult(
                    false,
                    verifiedCount,
                    prevSeq ?? 0,
                    prevHash ?? GrantRecordChain.GenesisHash,
                    r.Seq,
                    reason);
            }

            verifiedCount++;
            prevSeq = r.Seq;
            prevHash = r.RecordHash;
        }

        return new GrantChainVerificationResult(
            true,
            verifiedCount,
            prevSeq ?? 0,
            prevHash ?? GrantRecordChain.GenesisHash);
    }

    internal static bool ContainsGrantReference(string? detailsJson, Guid grantId)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "grants", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var inner in item.EnumerateObject())
                        {
                            if (string.Equals(inner.Name, "grantId", StringComparison.OrdinalIgnoreCase)
                                && inner.Value.ValueKind == JsonValueKind.String
                                && Guid.TryParse(inner.Value.GetString(), out var parsedId)
                                && parsedId == grantId)
                            {
                                return true;
                            }
                        }
                    }
                }
                else if (string.Equals(prop.Name, "grantId", StringComparison.OrdinalIgnoreCase)
                         && prop.Value.ValueKind == JsonValueKind.String
                         && Guid.TryParse(prop.Value.GetString(), out var rootGrantId)
                         && rootGrantId == grantId)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
