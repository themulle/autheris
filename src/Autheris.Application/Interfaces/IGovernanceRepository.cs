namespace Autheris.Application.Interfaces;

public interface ITableMetadataRepository
{
    Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default);
    Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default);
    Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Issue #94 (SD-5): replaces ONLY the declared primary key and indexes of a table. In one transaction the stored columns are read,
    /// the definition is validated against them and replaced, and the epoch is bumped once; masking rules and column metadata other than
    /// the key markers are untouched. Returns null when the table does not exist; throws <c>TableDefinitionException</c> when invalid.
    /// </summary>
    Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves an active table by identifier from its catalog row only: the stored key definition is NOT loaded or validated, so a
    /// table whose definition is invalid still resolves (an administrator must be able to replace it). Null when unknown or inactive.
    /// Default: via <see cref="GetTableMetadataAsync"/>, where an unavailable table resolves to null.
    /// </summary>
    async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
    {
        try
        {
            var existing = await GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            return existing is { Table.IsActive: true } ? existing.Identifier : null;
        }
        catch (Autheris.Domain.Exceptions.TableMetadataUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Resolves an active table by its id without loading every table (default: scan of <see cref="GetAllTablesAsync"/>).</summary>
    async Task<TableIdentifier?> GetActiveTableIdentifierByIdAsync(Guid tableId, CancellationToken ct = default)
    {
        var all = await GetAllTablesAsync(ct).ConfigureAwait(false);
        foreach (var t in all)
        {
            if (t.Table.Id == tableId)
            {
                return t.Identifier;
            }
        }

        return null;
    }
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

    /// <summary>
    /// SR-L-21: Tables on which <paramref name="tenantId"/> holds at least one non-revoked consent record (Allow or Deny).
    /// Consent-derived tenant ownership signal for catalog isolation (decision G-11: <c>TableMetadata</c> has no owner tenant).
    /// The default returns nothing (fail-closed: no consent-derived visibility).
    /// </summary>
    Task<IReadOnlyList<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TableIdentifier>>(Array.Empty<TableIdentifier>());

    /// <summary>
    /// WP-SC-02: active (non-revoked, in-window) consents of exactly one role grantee on one table within <paramref name="tenantId"/>.
    /// Role consents carry no subject SID, so the subject-based lookup never returns them. The default filters
    /// <see cref="GetAllActiveConsentsForSubjectsAsync(IEnumerable{Sid}, IEnumerable{string}?, DateTimeOffset?, TenantId?, CancellationToken)"/>;
    /// persistence overrides it with an indexed query.
    /// </summary>
    async Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(
        TableIdentifier table, string roleName, TenantId? tenantId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        var all = await GetAllActiveConsentsForSubjectsAsync(Array.Empty<Sid>(), new[] { roleName }, atTime, tenantId, ct).ConfigureAwait(false);
        return all
            .Where(c => c.GranteeType == GranteeType.Role
                && c.TableIdentifier.Equals(table)
                && string.Equals(c.RoleName, roleName, StringComparison.OrdinalIgnoreCase)
                && (tenantId is null || c.TenantId == tenantId.Value))
            .ToList();
    }

    /// <summary>
    /// WP-SC-10: one keyset page of the consents of a tenant (ordered by id), including role and group grants, optionally filtered by
    /// table and grantee. The tenant predicate is mandatory. Every persistence provider implements it with an indexed query; there is no
    /// default, so a consent listing can never load every consent into memory.
    /// </summary>
    Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(
        Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default);

    /// <summary>
    /// WP-SC-02: atomically revokes <paramref name="revokeConsentIds"/> and creates <paramref name="create"/> in ONE transaction
    /// with ONE epoch bump per affected table. Either every change is applied or none. There is no non-atomic fallback and no default: every provider
    /// must implement it transactionally, so a partial manifest can never be applied. <paramref name="auditEntries"/> are written in the SAME transaction (chain-linked, Tier-A anchored), so a committed change always has its audit record.
    /// </summary>
    Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default);
}


public interface IAuditLogRepository
{
    Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default);
    async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            await RecordAuditEventAsync(entries[i], ct).ConfigureAwait(false);
        }
    }
    Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default);
    Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default);

    /// <summary>
    /// WP-SC-10: one keyset page over the audit chain (newest first, by chain sequence). Rows without a sequence number are never
    /// listed. The default fails closed; every governance repository overrides it.
    /// </summary>
    Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement the audit cursor query; refusing an unbounded listing.");
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
    Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());
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
    /// SR-M-24: there is no safe default (filtering the unscoped LIMIT 1 lookup lets one tenant's ticket shadow another's),
    /// so every provider must override it with a scoped query; the default fails closed.
    /// </summary>
    Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement the tenant-scoped ticket lookup; refusing to fall back to an unscoped lookup.");

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
