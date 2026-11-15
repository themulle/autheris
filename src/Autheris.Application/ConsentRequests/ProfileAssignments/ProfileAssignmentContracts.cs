namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed record ProfileAssignmentSnapshotItem(
    int ItemNo,
    string Target,
    bool IsPattern,
    string? DataSource,
    IReadOnlyList<WriteRule> WriteRules);

public sealed record ProfileAssignmentRequestContext(
    Guid ProfileId,
    string ProfileName,
    MaskingPolicyMode MaskingMode,
    IReadOnlyList<GrantRowScopeEntry> RowScope,
    IReadOnlyList<ProfileAssignmentSnapshotItem> Items,
    bool IncludesFutureObjects = false,
    Guid? ExtendingConsentId = null,
    int? ExtendingItemNo = null);

public sealed record ProfileAssignmentItemRow(
    TenantId TenantId,
    Guid RequestId,
    int ItemNo,
    string ObjectRef,
    string OwnerSid,
    string OwnerDecision,
    string? DecidedBy = null,
    DateTimeOffset? DecidedAt = null);

public sealed record ProfileAssignmentSubmission(
    Guid ProfileId,
    Sid CallerSid,
    ClaimsPrincipal Principal,
    TenantId? ContextTenant = null,
    string? GranteeRef = null,
    string? GranteeType = null,
    string? Justification = null,
    string? RequestedDuration = null,
    DateTimeOffset? RequestedValidFrom = null,
    DateTimeOffset? RequestedValidTo = null,
    int? DurationDays = null,
    string? IdempotencyKey = null);

public interface IProfileAssignmentTransaction : IAsyncDisposable
{
    DbTransaction Transaction { get; }
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

public interface IProfileAssignmentStore
{
    Task<IProfileAssignmentTransaction> BeginTransactionAsync(CancellationToken ct = default);

    Task UpsertAssignmentItemsAsync(
        DbTransaction? transaction,
        IReadOnlyList<ProfileAssignmentItemRow> items,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProfileAssignmentItemRow>> GetAssignmentItemsAsync(
        DbTransaction? transaction,
        Guid requestId,
        CancellationToken ct = default);

    Task<int> DecideAssignmentItemsAsync(
        DbTransaction transaction,
        Guid requestId,
        IReadOnlyCollection<string> targetOwnerSids,
        string decision,
        string decidedBy,
        DateTimeOffset decidedAt,
        bool decideAllPending,
        CancellationToken ct = default);

    Task<IReadOnlyList<Consent>> GetConsentsByProfileAssignmentAsync(
        DbTransaction? transaction,
        TenantId tenantId,
        Guid profileAssignmentId,
        CancellationToken ct = default);

    Task<Consent?> GetConsentByIdInTransactionAsync(
        DbTransaction transaction,
        Guid consentId,
        CancellationToken ct = default);

    Task MarkConsentRevokedInTransactionAsync(
        DbTransaction transaction,
        Guid consentId,
        Sid actorSid,
        string reason,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task AddApprovingOwnersInTransactionAsync(
        DbTransaction transaction,
        Consent consent,
        IReadOnlyCollection<string> ownerSids,
        CancellationToken ct = default);

    Task IncrementTableEpochInTransactionAsync(
        DbTransaction transaction,
        TableIdentifier table,
        CancellationToken ct = default);

    Task RecordAuditInTransactionAsync(
        DbTransaction transaction,
        AuditLogEntry entry,
        CancellationToken ct = default);

    Task ApplyRequestStepAndStatusInTransactionAsync(
        DbTransaction transaction,
        ConsentRequest req,
        string? newStatus,
        Sid approverSid,
        string stepDecision,
        string? reason,
        DateTimeOffset decidedAt,
        CancellationToken ct = default);

    Task InvalidateTablesAfterCommitAsync(
        IReadOnlyCollection<TableIdentifier> tables,
        CancellationToken ct = default);
}

public interface IProfileAssignmentRequestService
{
    Task<SubmitConsentRequestResult> SubmitAsync(
        ProfileAssignmentSubmission submission,
        CancellationToken ct = default);

    Task HandlePatternNewOwnerAsync(
        Consent patternConsent,
        TableMetadata newTable,
        IReadOnlyCollection<string> ownerSids,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<int> RevokeAssignmentAsync(
        TenantId tenantId,
        Guid profileAssignmentId,
        Sid actorSid,
        string reason,
        CancellationToken ct = default);
}

public interface IProfileAssignmentDecisionHandler
{
    Task<ConsentDecisionResult> DecideAsync(
        ConsentDecisionCommand command,
        ConsentRequest req,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        CancellationToken ct = default);
}

public interface IProfileGrantMaterializer
{
    Task<IReadOnlyList<Consent>> MaterializeApprovedAsync(
        DbTransaction transaction,
        ConsentRequest req,
        ProfileAssignmentRequestContext context,
        IReadOnlyList<ProfileAssignmentItemRow> itemRows,
        IReadOnlyList<TableMetadata> tenantTables,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        DateTimeOffset approvedValidTo,
        DateTimeOffset decidedAt,
        string? description,
        IReadOnlyList<WriteRule>? narrowedWriteRules,
        IReadOnlyList<int>? approvedItemNos,
        IReadOnlyList<string>? approvedClearColumns,
        CancellationToken ct = default);

    Task<Consent> ExtendApprovingOwnersAsync(
        DbTransaction transaction,
        Consent existingPatternConsent,
        Guid requestId,
        IReadOnlyCollection<string> newApprovingOwners,
        Sid decidedBy,
        DateTimeOffset decidedAt,
        CancellationToken ct = default);

    Task<int> RevokeAssignmentAsync(
        TenantId tenantId,
        Guid profileAssignmentId,
        Sid actorSid,
        string reason,
        CancellationToken ct = default);
}
