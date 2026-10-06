namespace Autheris.Application.Interfaces;

public interface ITableMetadataRepository
{
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default);
    Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default);
}

public interface IConsentRepository
{
    Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default);
    Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default);
    Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default);
    Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default);
    Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default);

    /// <summary>
    /// SEC E-05 / EX-02: All non-revoked, not yet expired consents carrying <paramref name="consentRequestId"/>
    /// (e.g. the OpenMetadata sync marker) – without any subject, role, table or tenant filter (used for reconcile).
    /// </summary>
    Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default);

    /// <summary>
    /// SEC E-05 / EX-02: System revocation of a consent owned by an automated sync. Only revokes when the consent carries
    /// <paramref name="consentRequestId"/> (the sync marker); there is no data-owner check because the system actor is not
    /// a table owner and a revocation can only tighten access. Returns false when nothing was revoked.
    /// </summary>
    Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default);
}


public interface IAuditLogRepository
{
    Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default);
    Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default);
}

public interface IPolicyEpochRepository
{
    Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default);
}

public interface IConsentApprovalRepository
{
    Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default);
    Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default);
    Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default);
    Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default);
    Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, ct);

    /// <summary>
    /// Review R3-1: ITSM approval with the approver (account or e-mail) reported by the ITSM system, passed typed.
    /// Implementations must check it against owners/delegates; the default keeps the previous behaviour.
    /// </summary>
    Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, ct);
    Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default);

    /// <summary>
    /// Review G5: rejection with an explicit flag for callers that act on behalf of the verified external ITSM system
    /// (signed webhook). Authorization never infers this from the SID prefix. The default ignores the flag.
    /// </summary>
    Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
        => RejectConsentRequestAsync(requestId, approverSid, reason, ct);

    /// <summary>
    /// Review G5: activation for the insecure getting-started auto-approve path (request still pending).
    /// Regular activation (<see cref="ActivateConsentAsync(Guid, Sid?, CancellationToken)"/>) only accepts 'APPROVED'.
    /// </summary>
    Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, ct);
    Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default);
    Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default);
    Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default);
    Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default);

    /// <summary>
    /// SEC H-06: Tenant-bound ticket lookup (<c>WHERE itsm_ticket_id = @t AND tenant_id = @tenant</c>).
    /// The default implementation filters the unscoped lookup; persistent repositories override it with a scoped query.
    /// </summary>
    async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
    {
        var request = await GetConsentRequestByTicketIdAsync(ticketId, ct).ConfigureAwait(false);
        return request != null && request.TenantId == tenantId ? request : null;
    }

    /// <summary>
    /// SEC H-06: Activates a pending consent request and records <paramref name="approvedBy"/> as audit actor
    /// (e.g. the ITSM instance/approver instead of the requester). Activation only happens from a pending status.
    /// </summary>
    Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, ct);
}

public interface IDataOwnershipRepository
{
    Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default);
    Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default);
    Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetTransitiveRolesAsync(Sid subjectSid, CancellationToken ct = default);
}

public interface ITableRelationRepository
{
    Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default);
    Task CreateRelationAsync(TableRelation relation, CancellationToken ct = default);
}

public interface IGovernanceRepository :
    ITableMetadataRepository,
    IConsentRepository,
    IAuditLogRepository,
    IPolicyEpochRepository,
    IConsentApprovalRepository,
    IDataOwnershipRepository,
    ITableRelationRepository,
    IItsmOutboxRepository
{
}
