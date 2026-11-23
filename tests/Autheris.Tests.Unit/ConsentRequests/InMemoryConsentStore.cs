namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

internal sealed class InMemoryConsentStore(
    IGrantRecordSigner signer,
    TimeProvider clock)
    : IConsentApprovalRepository,
      IConsentRepository,
      IConsentWorkflowConfigRepository,
      IConsentEventOutbox,
      IGrantRecordStore,
      ITableMetadataRepository,
      IDataOwnershipRepository,
      IAuditLogRepository
{
    private readonly TableMetadata _ordersTable = new()
    {
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SchemaName = "public",
            TableName = "orders",
            Description = "Orders"
        },
        Identifier = new TableIdentifier("pg", "public", "orders"),
        Columns =
        [
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "bigint" }
        ]
    };

    private readonly TableMetadata _hiddenTable = new()
    {
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SchemaName = "public",
            TableName = "hidden_payroll",
            Description = "Hidden",
            DiscoveryHidden = true
        },
        Identifier = new TableIdentifier("pg", "public", "hidden_payroll"),
        Columns =
        [
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "bigint" }
        ]
    };

    public List<ConsentRequest> Requests { get; } = new();
    public List<Consent> Consents { get; } = new();
    public List<ExternalWorkflowSystem> Systems { get; } = new();
    public List<GrantRecord> GrantRecords { get; } = new();
    public List<ConsentEventRecord> Events { get; } = new();

    public Guid RegisterExternalSystem(string tenantId, string clientId, bool enabled)
    {
        var id = Guid.NewGuid();
        Systems.Add(new ExternalWorkflowSystem(id, tenantId, clientId, clientId, "https://example.invalid/hook", "secret-ref", enabled, "admin", clock.GetUtcNow(), enabled ? null : clock.GetUtcNow()));
        return id;
    }

    public ConsentRequest SeedExternalRequest(string tenantId, Guid externalSystemId, string externalRef)
    {
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = _ordersTable.Table.Id,
            TableIdentifier = _ordersTable.Identifier,
            RequesterSid = new Sid("S-1-5-21-REQUESTER-1"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REQUESTER-1",
            BusinessJustification = "External ticket access",
            Status = "PENDING_EXTERNAL_APPROVAL",
            RequestedAt = clock.GetUtcNow().AddHours(-1),
            RequestedValidFrom = clock.GetUtcNow().AddHours(-1),
            RequestedValidTo = clock.GetUtcNow().AddDays(30),
            UpdatedAt = clock.GetUtcNow().AddHours(-1),
            TenantId = new TenantId(tenantId),
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = externalSystemId,
            ExternalReference = externalRef,
            Version = 1
        };
        Requests.Add(req);
        return req;
    }

    public void AddEvent(string tenantId, long seq, string type, string subjectId, string payloadJson, DateTimeOffset createdAt, Guid? externalSystemId = null) =>
        Events.Add(new ConsentEventRecord(tenantId, seq, Guid.NewGuid(), type, subjectId, payloadJson, createdAt));

    public Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default)
    {
        var sys = Systems.FirstOrDefault(s => s.TenantId == tenantId && s.Enabled);
        return Task.FromResult<ConsentWorkflowRoute?>(sys is null ? null : new ConsentWorkflowRoute(tenantId.Value, WorkflowRouteScopeKind.Tenant, "*", ConsentWorkflowMode.External, sys.Id, 90, 30, 24, false, "admin", clock.GetUtcNow()));
    }

    public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, CancellationToken ct = default) =>
        Task.FromResult(Systems.FirstOrDefault(s => s.TenantId == tenantId && s.Id == systemId));

    public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemByClientIdAsync(TenantId tenantId, string clientId, CancellationToken ct = default) =>
        Task.FromResult(Systems.FirstOrDefault(s => s.TenantId == tenantId && string.Equals(s.ClientId, clientId, StringComparison.OrdinalIgnoreCase)));

    public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(Requests.FirstOrDefault(r => r.Id == requestId));

    public Task<ConsentRequestPage> QueryTenantConsentRequestsAsync(ConsentRequestPageQuery query, CancellationToken ct = default)
    {
        var filtered = Requests.Where(r => r.TenantId == query.TenantId
            && (!query.ExternalSystemId.HasValue || r.ExternalSystemId == query.ExternalSystemId)
            && (string.IsNullOrWhiteSpace(query.RequesterSid) || r.RequesterSid.Value == query.RequesterSid)
            && (string.IsNullOrWhiteSpace(query.Status) || string.Equals(r.Status, query.Status, StringComparison.OrdinalIgnoreCase))).ToList();
        return Task.FromResult(new ConsentRequestPage(filtered, null));
    }

    public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        var stored = new ConsentRequest
        {
            Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            TableId = _ordersTable.Table.Id,
            TableIdentifier = request.TableIdentifier,
            RequesterSid = request.RequesterSid,
            RequestedGranteeType = request.RequestedGranteeType,
            RequestedGranteeRef = request.RequestedGranteeRef,
            BusinessJustification = request.BusinessJustification,
            Status = request.Status,
            RequestedAt = request.RequestedAt,
            RequestedValidFrom = request.RequestedValidFrom ?? request.RequestedAt,
            RequestedValidTo = request.RequestedValidTo,
            UpdatedAt = clock.GetUtcNow(),
            TenantId = request.TenantId,
            Version = 1,
            WorkflowMode = request.WorkflowMode,
            ExternalSystemId = request.ExternalSystemId
        };
        Requests.Add(stored);
        return Task.FromResult(stored);
    }

    /// <summary>Evaluates the decision like the engine's transaction would; the effect is never applied (this store has no grant writer).</summary>
    public async Task<Autheris.Application.Governance.Requests.DecisionCasResult> TryDecideAsync(
        Guid requestId,
        int expectedVersion,
        Func<Autheris.Application.Governance.Requests.IGovernanceTransaction, ConsentRequest, Task<Autheris.Application.Governance.Requests.PermissionDecisionRecord>> decideInTransaction,
        Func<Autheris.Application.Governance.Requests.IGovernanceTransaction, Autheris.Application.Governance.Requests.PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<Autheris.Application.Governance.Requests.GrantRef>>> applyEffect,
        CancellationToken ct = default)
    {
        var request = Requests.FirstOrDefault(r => r.Id == requestId);
        if (request is null)
        {
            return new Autheris.Application.Governance.Requests.DecisionCasResult(Autheris.Application.Governance.Requests.DecisionCasOutcome.NotFound);
        }

        if (request.Version != expectedVersion)
        {
            return new Autheris.Application.Governance.Requests.DecisionCasResult(Autheris.Application.Governance.Requests.DecisionCasOutcome.AlreadyDecided, request);
        }

        await decideInTransaction(Autheris.Tests.Unit.Security.NullFake.Create<Autheris.Application.Governance.Requests.IGovernanceTransaction>(), request);
        throw new NotSupportedException("InMemoryConsentStore evaluates decisions but never applies their effect.");
    }

    public Task<bool> WithdrawConsentRequestAsync(Guid requestId, Sid requesterSid, CancellationToken ct = default)
    {
        var existing = Requests.FirstOrDefault(r => r.Id == requestId && r.RequesterSid == requesterSid && r.Status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase));
        if (existing is null) return Task.FromResult(false);
        existing.Status = "WITHDRAWN";
        return Task.FromResult(true);
    }

    public Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        var idx = Requests.FindIndex(r => r.Id == requestId);
        if (idx < 0)
        {
            throw new InvalidOperationException($"Request {requestId} not found.");
        }

        var existing = Requests[idx];
        if (existing.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in internal workflow mode.");
        }

        if (!string.Equals(existing.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentRequestStatusConflictException(requestId);
        }

        if (existing.ApprovalSteps.Any(s => string.Equals(s.ApproverSid.Value, effectiveApproverSid.Value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("FOUR_EYES_SAME_APPROVER: approver has already approved this request.");
        }

        var step = new ApprovalStep
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = requestId,
            StepNumber = existing.ApprovalSteps.Count + 1,
            ApproverSid = effectiveApproverSid,
            Decision = "APPROVED",
            DecidedAt = attribution.DecidedAt
        };
        var updatedSteps = existing.ApprovalSteps.Concat([step]).ToList();
        var updated = CloneRequestWith(existing, "PENDING_EXTERNAL_APPROVAL", existing.Version + 1, updatedSteps);
        Requests[idx] = updated;
        return Task.FromResult(updated);
    }

    public Task<Consent> ExecuteExternalAtomicApprovalWithConsentAsync(Guid requestId, ExternalDecisionAttribution attribution, ApprovalStep step, Consent consent, IReadOnlyList<ConsentRowFilter> rowFilters, IReadOnlyList<ConsentColumnRule> columnRules, CancellationToken ct = default)
    {
        var idx = Requests.FindIndex(r => r.Id == requestId);
        var existing = Requests[idx];
        var updated = CloneRequestWith(existing, "APPROVED", existing.Version + 1, existing.ApprovalSteps.Concat([step]).ToList());
        Requests[idx] = updated;
        Consents.Add(consent);
        AppendGrantRecord(consent, GrantRecordType.Issued, step.ApproverSid.Value);
        return Task.FromResult(consent);
    }

    public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(Guid requestId, ApprovalStep step, Consent consent, IReadOnlyList<ConsentRowFilter> rowFilters, IReadOnlyList<ConsentColumnRule> columnRules, CancellationToken ct = default) =>
        ExecuteExternalAtomicApprovalWithConsentAsync(requestId, new ExternalDecisionAttribution(Guid.Empty, string.Empty, "APPROVE", null, clock.GetUtcNow()), step, consent, rowFilters, columnRules, ct);

    public Task<ConsentRequest> RejectExternalConsentRequestAsync(Guid requestId, ExternalDecisionAttribution attribution, Sid effectiveApproverSid, string reason, CancellationToken ct = default)
    {
        var idx = Requests.FindIndex(r => r.Id == requestId);
        var existing = Requests[idx];
        var updated = CloneRequestWith(existing, "REJECTED", existing.Version + 1, existing.ApprovalSteps);
        Requests[idx] = updated;
        return Task.FromResult(updated);
    }

    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
        Task.FromResult(Consents.FirstOrDefault(c => c.Id == consentId));

    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        var idx = Consents.FindIndex(c => c.Id == consentId && c.ConsentRequestId == consentRequestId && !c.IsRevoked);
        if (idx < 0) return Task.FromResult(false);
        Consents[idx] = CloneConsentRevoked(Consents[idx], reason, clock.GetUtcNow());
        AppendGrantRecord(Consents[idx], GrantRecordType.Revoked, revokedBySid.Value);
        return Task.FromResult(true);
    }

    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default)
    {
        var idx = Consents.FindIndex(c => c.Id == consentId);
        if (idx >= 0)
        {
            Consents[idx] = CloneConsentRevoked(Consents[idx], reason, clock.GetUtcNow());
            AppendGrantRecord(Consents[idx], GrantRecordType.Revoked, revokedBySid.Value);
        }

        return Task.CompletedTask;
    }

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) =>
        Task.FromResult(new ConsentPage(Consents.Where(c => c.TenantId == query.Tenant && (query.IncludeInactive || !c.IsRevoked)).ToList(), null));

    public Task<ConsentEventFeedPage> ReadFeedAsync(TenantId tenantId, long sinceSeq, int pageSize, Guid? externalSystemId = null, DateTimeOffset? retentionCutoff = null, CancellationToken ct = default)
    {
        var tenantEvents = Events.Where(e => e.TenantId == tenantId.Value).OrderBy(e => e.Seq).ToList();
        if (tenantEvents.Count > 0 && sinceSeq < tenantEvents[0].Seq - 1)
        {
            return Task.FromResult(new ConsentEventFeedPage(Array.Empty<ConsentEventRecord>(), sinceSeq, CursorExpired: true));
        }

        var page = tenantEvents.Where(e => e.Seq > sinceSeq).Take(pageSize).ToList();
        return Task.FromResult(new ConsentEventFeedPage(page, page.Count > 0 ? page[^1].Seq : sinceSeq, CursorExpired: false));
    }

    public Task<long> AppendAsync(ConsentEventWrite entry, DbTransaction? transaction = null, CancellationToken ct = default)
    {
        var nextSeq = Events.Count + 1L;
        Events.Add(new ConsentEventRecord(entry.TenantId, nextSeq, entry.EventId, entry.EventType, entry.SubjectId, entry.PayloadJson, entry.CreatedAt));
        return Task.FromResult(nextSeq);
    }

    public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GrantRecord>>(GrantRecords.Where(r => r.TenantId == tenantId).OrderBy(r => r.Seq).ToList());

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
        Task.FromResult<TableMetadata?>(table.Equals(_ordersTable.Identifier) ? _ordersTable : table.Equals(_hiddenTable.Identifier) ? _hiddenTable : null);

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, TenantId? tenantId, CancellationToken ct = default) =>
        GetTableMetadataAsync(table, ct);

    public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.RequesterSid == requesterSid && (r.TenantId == default || r.TenantId == tenantId)).ToList());
    public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());
    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateRequestStatusAsync(Guid requestId, string status, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateRequestStatusAsync(Guid requestId, string status, string? itsmTicketId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ConsentRequest>> GetExpiredPendingRequestsAsync(DateTimeOffset beforeTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());
    public Task<IReadOnlyList<ConsentRequest>> GetTimedOutRequestsAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsentRequest>>(Array.Empty<ConsentRequest>());
    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());
    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());
    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());
    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>());
    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, TenantId? expectedTenant, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([_ordersTable]);
    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata table, CancellationToken ct = default) => Task.FromResult(table);
    public Task<bool> DeleteTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(false);
    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataOwner>>(Array.Empty<DataOwner>());
    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<TableIdentifier>> GetTablesOwnedOrDelegatedAsync(Sid userSid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableIdentifier>>(Array.Empty<TableIdentifier>());
    public Task<DataOwner> RegisterDataOwnerAsync(DataOwner owner, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AssignTableOwnerAsync(TableIdentifier table, Guid ownerId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction transaction, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());
    public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? eventType = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());
    public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default) => Task.FromResult((0L, GrantRecordChain.GenesisHash));
    public Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<PendingConsentEventDelivery>> ClaimDeliveriesAsync(int limit, DateTimeOffset now, TimeSpan leaseDuration, string leaseOwner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingConsentEventDelivery>>(Array.Empty<PendingConsentEventDelivery>());
    public Task MarkDeliveredAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset deliveredAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task RescheduleDeliveryAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset nextAttemptAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkDeadLetteredAsync(Guid eventId, Guid externalSystemId, string leaseOwner, DateTimeOffset deadLetteredAt, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PurgeExpiredEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);

    private void AppendGrantRecord(Consent consent, GrantRecordType type, string actor)
    {
        var seq = GrantRecords.Count + 1L;
        var prevHash = GrantRecords.Count == 0 ? GrantRecordChain.GenesisHash : GrantRecords[^1].RecordHash;
        var unsigned = new GrantRecord(
            consent.TenantId.Value, seq, Guid.NewGuid(), type, consent.Id, consent.ConsentRequestId,
            consent.GrantSource, consent.ProfileAssignmentId, consent.GranteeType.ToString(),
            consent.GranteeSid?.Value ?? string.Empty, consent.TableIdentifier.ToString(),
            "{\"allow\":true}", consent.ValidFrom, consent.ValidTo, actor, "{}", clock.GetUtcNow(), clock.GetUtcNow(), prevHash);
        var hash = GrantRecordChain.ComputeRecordHash(unsigned);
        var sig = signer.Sign(System.Text.Encoding.UTF8.GetBytes(hash));
        GrantRecords.Add(unsigned with { RecordHash = hash, Signature = sig.Signature, KeyId = sig.KeyId });
    }

    private static ConsentRequest CloneRequestWith(ConsentRequest r, string status, int version, List<ApprovalStep> steps) =>
        new()
        {
            Id = r.Id, TableId = r.TableId, TableIdentifier = r.TableIdentifier, RequesterSid = r.RequesterSid,
            RequestedGranteeType = r.RequestedGranteeType, RequestedGranteeRef = r.RequestedGranteeRef,
            BusinessJustification = r.BusinessJustification, Status = status, RequestedAt = r.RequestedAt,
            RequestedValidFrom = r.RequestedValidFrom, RequestedValidTo = r.RequestedValidTo, UpdatedAt = r.UpdatedAt,
            TenantId = r.TenantId, WorkflowMode = r.WorkflowMode, ExternalSystemId = r.ExternalSystemId,
            ExternalReference = r.ExternalReference, Version = version, ApprovalSteps = steps
        };

    private static Consent CloneConsentRevoked(Consent c, string reason, DateTimeOffset revokedAt) =>
        new()
        {
            Id = c.Id, TableId = c.TableId, TableIdentifier = c.TableIdentifier, ConsentRequestId = c.ConsentRequestId,
            Effect = c.Effect, GranteeType = c.GranteeType, GranteeSid = c.GranteeSid,
            ValidFrom = c.ValidFrom, ValidTo = c.ValidTo, IsRevoked = true, RevokedAt = revokedAt,
            RevokeReason = reason, TenantId = c.TenantId, GrantSource = c.GrantSource, GrantRecordSeq = c.GrantRecordSeq
        };
}
