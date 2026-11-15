namespace Autheris.Application.Governance.Grants.Records;

using System;
using System.Collections.Generic;
using System.Linq;
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
}

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
    IReadOnlyList<GrantRecord>? Records = null);

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

        return await _chainVerifier.VerifyTenantChainAsync(tenantId.Value, ct).ConfigureAwait(false);
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
