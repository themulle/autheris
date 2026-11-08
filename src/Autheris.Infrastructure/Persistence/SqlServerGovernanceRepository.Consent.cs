namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public partial class SqlServerGovernanceRepository
{
    internal Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam
    {
        get => _governanceRepository.ConsentStatusUpdateSeam;
        set => _governanceRepository.ConsentStatusUpdateSeam = value;
    }

    internal static class ConsentGrantSql
    {
        public static string ActiveRoleConsents(bool withTenant) => @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE t.source_name_lc = LOWER(@domain) AND t.schema_name_lc = LOWER(@schema) AND t.table_name_lc = LOWER(@table)
                                  AND c.is_revoked = 0" + (withTenant ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND LOWER(c.role_name) = LOWER(@role)";

        public const string RevokeTarget = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c WITH (UPDLOCK, ROWLOCK)
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.id = @id";
    }

    internal static class ConsentSql
    {
        public const string FindTableIdSql = GovernanceRepository.ConsentSql.FindTableIdSql;
        public const string SelectExpiringConsentsSql = GovernanceRepository.ConsentSql.SelectExpiringConsentsSql;
        public const string SelectConsentRequestByIdSql = GovernanceRepository.ConsentSql.SelectConsentRequestByIdSql;
        public const string SelectPendingRequestsForApproverSql = GovernanceRepository.ConsentSql.SelectPendingRequestsForApproverSql;
        public const string CountConfiguredOwnersSql = GovernanceRepository.ConsentSql.CountConfiguredOwnersSql;
        public const string SelectRequestIdByTicketSql = GovernanceRepository.ConsentSql.SelectRequestIdByTicketSql;
        public const string SelectRequestIdByTicketAndTenantSql = GovernanceRepository.ConsentSql.SelectRequestIdByTicketAndTenantSql;
        public const string UpdateSystemRevokeSql = GovernanceRepository.ConsentSql.UpdateSystemRevokeSql;
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, ct);
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetActiveConsentsForSubjectsAsync(subjects, table, atTime, tenantId, ct);
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, ct);
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, tenantId, ct);
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetActiveConsentsByConsentRequestIdAsync(consentRequestId, atTime, ct);
    }

    public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.LoadDescriptionsAsync(consents, ct);
    }

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.CreateConsentAsync(consent, ct);
    }

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetConsentByIdAsync(consentId, ct);
    }

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RevokeSystemConsentAsync(consentId, consentRequestId, revokedBySid, reason, ct);
    }

    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetExpiringConsentsAsync(threshold, maxResults, ct);
    }

    public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ExtendConsentExpiryAsync(consentId, newValidTo, ct);
    }

    public Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(Guid rowFilterId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetRowFilterAsync(rowFilterId, ct);
    }

    public Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.UpdateRowFilterEvaluationAsync(rowFilterId, tenant, settings, ct);
    }

    public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.CreateConsentRequestAsync(request, ct);
    }

    public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetConsentRequestAsync(requestId, ct);
    }

    public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetRequestsByRequesterSidAsync(requesterSid, status, ct);
    }

    public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetPendingRequestsForApproverAsync(approverSid, ct);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ApproveConsentRequestStepAsync(requestId, approverSid, ct);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, ct);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount, ct);
    }

    public Task<bool> WithdrawConsentRequestAsync(Guid requestId, Sid requesterSid, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.WithdrawConsentRequestAsync(requestId, requesterSid, ct);
    }

    public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(Guid requestId, Sid approverSid, Consent consent, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ExecuteAtomicApprovalWithConsentAsync(requestId, approverSid, consent, ct);
    }

    public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(Guid requestId, ApprovalStep step, Consent consent, IReadOnlyList<ConsentRowFilter> rowFilters, IReadOnlyList<ConsentColumnRule> columnRules, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ExecuteAtomicApprovalWithConsentAsync(requestId, step, consent, rowFilters, columnRules, ct);
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RejectConsentRequestAsync(requestId, approverSid, reason, ct);
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm, ct);
    }

    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetConsentRequestByTicketIdAsync(ticketId, ct);
    }

    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetConsentRequestByTicketIdAsync(ticketId, tenantId, ct);
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ActivateConsentAsync(requestId, ct);
    }

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ActivateConsentAsync(requestId, approvedBy, ct);
    }

    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ActivateConsentForAutoApproveAsync(requestId, ct);
    }

    public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.DeleteConsentRequestAsync(requestId, ct);
    }

    public Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.UpdateConsentRequestTicketIdAsync(requestId, ticketId, ct);
    }

    public Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(TableIdentifier table, string roleName, TenantId? tenantId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetActiveRoleConsentsAsync(table, roleName, tenantId, atTime, ct);
    }

    internal Task<bool> IsAuthorizedApproverForTableInternalAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct) =>
        _governanceRepository.IsAuthorizedApproverForTableInternalAsync(conn, tx, table, approverSid, itsmApproverAccount, ct);

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, ct);
    }

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, descriptionUpdates, ct);
    }

    public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.UpdateConsentDescriptionWithAuditAsync(consentId, tenant, description, audit, ct);
    }

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.RevokeConsentAsync(consentId, revokedBySid, reason, ct);
    }

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return _governanceRepository.GetConsentPageAsync(query, ct);
    }
}
