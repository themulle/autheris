namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
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

public interface IProfileAssignmentTransaction : IAsyncDisposable
{
    DbTransaction Transaction { get; }
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

/// <summary>A consent revoked by a profile-assignment revocation, with its table for event routing.</summary>
public readonly record struct RevokedProfileGrant(Guid ConsentId, TableIdentifier Table);

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

    /// <summary>Returns true only when the conditional update matched an active consent.</summary>
    Task<bool> MarkConsentRevokedInTransactionAsync(
        DbTransaction transaction,
        TenantId tenantId,
        Guid consentId,
        Sid actorSid,
        string reason,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Appends one grant.revoked event per revoked grant inside the revoke transaction, after all its other writes.
    /// An empty list is a no-op.
    /// </summary>
    Task AppendGrantRevokedEventsInTransactionAsync(
        DbTransaction transaction,
        TenantId tenantId,
        IReadOnlyList<RevokedProfileGrant> revokedGrants,
        DateTimeOffset occurredAt,
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

public interface IProfileAssignmentMaintenanceService
{
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

/// <summary>Reads the snapshot stored in the context of a profile assignment request.</summary>
public static class ProfileAssignmentSnapshots
{
    public static ProfileAssignmentRequestContext Parse(ConsentRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.ContextJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ProfileAssignmentRequestContext>(req.ContextJson);
                if (parsed is { Items.Count: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }

        var fallbackTarget = req.TableIdentifier.ToString();
        return new ProfileAssignmentRequestContext(
            req.ProfileId ?? req.Id,
            "AccessProfile",
            MaskingPolicyMode.Default,
            [],
            [new ProfileAssignmentSnapshotItem(0, fallbackTarget, fallbackTarget.Contains('*', StringComparison.Ordinal), req.TableIdentifier.Domain, req.RequestedWriteRules ?? [])]);
    }
}
