namespace Autheris.Application.Governance.Grants.Records;

using System.Data.Common;
using System.Text;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

public sealed class GrantWriter(IGrantRecordStore store, IGrantRecordSigner signer, TimeProvider? timeProvider = null) : IGrantWriter
{
    private readonly IGrantRecordStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IGrantRecordSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var type = input?.RecordType is GrantRecordType.Renewed ? GrantRecordType.Renewed : GrantRecordType.Issued;
        return AppendAsync(transaction, BuildRecord(consent, input, type), consent.Id, ct);
    }

    public Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) =>
        AppendAsync(transaction, BuildRecord(consent ?? throw new ArgumentNullException(nameof(consent)), input, GrantRecordType.Revoked), null, ct);

    public Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default) =>
        AppendAsync(transaction, BuildRecord(null, input ?? throw new ArgumentNullException(nameof(input)), GrantRecordType.Rejected), null, ct);

    public Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default) =>
        AppendAsync(transaction, BuildRecord(consent, input ?? throw new ArgumentNullException(nameof(input)), consent is not null ? GrantRecordType.Expired : input.RecordType ?? GrantRecordType.RequestExpired), null, ct);

    private async Task<GrantRecord> AppendAsync(DbTransaction transaction, GrantRecord draft, Guid? updateConsentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (string.IsNullOrWhiteSpace(draft.TenantId)) throw new ArgumentException("TenantId is required.", nameof(draft));
        _signer.EnsureSigningKeyAvailable();
        var (lastSeq, lastHash) = await _store.LockAndReadHeadAsync(transaction, draft.TenantId, ct).ConfigureAwait(false);
        var baseRecord = draft with { Seq = lastSeq + 1, PrevHash = string.IsNullOrWhiteSpace(lastHash) ? GrantRecordChain.GenesisHash : lastHash, RecordedAt = _timeProvider.GetUtcNow() };
        var recordHash = GrantRecordChain.ComputeRecordHash(baseRecord);

        GrantSignatureResult sig;
        try { sig = _signer.Sign(Encoding.UTF8.GetBytes(recordHash)); }
        catch (Exception ex) when (ex is not OperationCanceledException and not GrantRecordUnavailableException) { throw new GrantRecordUnavailableException("Grant record signing failed (fail-closed).", ex); }

        var signed = baseRecord with { RecordHash = recordHash, Signature = sig.Signature, KeyId = sig.KeyId };
        await _store.AppendRecordAndAdvanceHeadAsync(transaction, signed, ct).ConfigureAwait(false);
        if (updateConsentId is { } cid) await _store.UpdateConsentGrantRecordSeqAsync(transaction, signed.TenantId, cid, signed.Seq, ct).ConfigureAwait(false);
        return signed;
    }

    private GrantRecord BuildRecord(Consent? consent, GrantRecordInput? input, GrantRecordType recordType)
    {
        var tenantId = consent?.TenantId.Value ?? input?.TenantId ?? string.Empty;
        var objectId = !string.IsNullOrWhiteSpace(input?.ObjectId) ? input.ObjectId : consent is not null ? (!string.IsNullOrWhiteSpace(consent.TableIdentifier.TableName) ? consent.TableIdentifier.ToString() : consent.TableId.ToString("D")) : null;
        var (granteeType, granteeRef) = (input?.GranteeType ?? consent?.GranteeType.ToString(), input?.GranteeRef ?? consent?.GranteeSid);
        if (string.IsNullOrWhiteSpace(objectId) || string.IsNullOrWhiteSpace(granteeType) || string.IsNullOrWhiteSpace(granteeRef))
            throw new ArgumentException("ObjectId, GranteeType, and GranteeRef are required.", nameof(input));
        return new GrantRecord(tenantId, 0, Guid.CreateVersion7(), recordType, consent?.Id, input?.RequestId ?? consent?.ConsentRequestId,
            input?.GrantSource ?? consent?.GrantSource ?? ConsentGrantSource.ConsentRequest, input?.ProfileAssignmentId ?? consent?.ProfileAssignmentId,
            granteeType, granteeRef, objectId, string.IsNullOrWhiteSpace(input?.ScopeJson) ? "{}" : input.ScopeJson,
            input?.ValidFrom ?? consent?.ValidFrom, input?.ValidUntil ?? consent?.ValidTo,
            !string.IsNullOrWhiteSpace(input?.RequestedBy) ? input.RequestedBy : consent?.CreatedBySid ?? string.Empty,
            string.IsNullOrWhiteSpace(input?.DecisionJson) ? "{}" : input.DecisionJson,
            input?.DecidedAt ?? _timeProvider.GetUtcNow(), DateTimeOffset.MinValue, GrantRecordChain.GenesisHash);
    }
}
