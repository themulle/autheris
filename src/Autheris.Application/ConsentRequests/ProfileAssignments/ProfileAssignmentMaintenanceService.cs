namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Housekeeping for profile assignments that stay outside the permission request engine: revoking an assignment and
/// queueing owner approval when a pattern grant matches a newly created object.
/// </summary>
public sealed class ProfileAssignmentMaintenanceService(
    IConsentApprovalRepository approvalRepository,
    IProfileAssignmentStore store,
    IProfileGrantMaterializer materializer,
    IGrantPolicy grantPolicy,
    IConsentEventOutbox? eventOutbox = null) : IProfileAssignmentMaintenanceService
{
    private readonly IConsentApprovalRepository _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
    private readonly IProfileAssignmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProfileGrantMaterializer _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    private readonly IGrantPolicy _grantPolicy = grantPolicy ?? throw new ArgumentNullException(nameof(grantPolicy));
    private readonly IConsentEventOutbox? _eventOutbox = eventOutbox;

    /// <summary>
    /// Requester of system-initiated pattern coverage requests. Using a system identity keeps these requests out of the
    /// grantee's pending-request cap; separation of duties still checks the grantee through the requested grantee.
    /// </summary>
    public static readonly Sid PatternCoverageRequester = new("system:pattern-coverage");

    public async Task HandlePatternNewOwnerAsync(
        Consent patternConsent,
        TableMetadata newTable,
        IReadOnlyCollection<string> ownerSids,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patternConsent);
        ArgumentNullException.ThrowIfNull(newTable);
        if (patternConsent.ProfileAssignmentId is not { } assignmentId ||
            patternConsent.WorkflowMode != ConsentWorkflowMode.Internal ||
            ownerSids is not { Count: > 0 })
        {
            return;
        }

        var missingOwners = ownerSids
            .Where(s => !string.IsNullOrWhiteSpace(s) &&
                        !patternConsent.ApprovingOwners.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingOwners.Count == 0)
        {
            return;
        }

        if (patternConsent.GranteeSid is not { } grantee || string.IsNullOrWhiteSpace(grantee.Value))
        {
            return;
        }

        // The assignment id is the id of the original profile assignment request; the request carries the real profile id.
        var assignment = await _approvalRepository.GetConsentRequestAsync(assignmentId, ct).ConfigureAwait(false);
        if (assignment is null ||
            assignment.TenantId != patternConsent.TenantId ||
            assignment.RequestKind != ConsentRequestKind.ProfileAssignment ||
            assignment.ProfileId is not { } profileId ||
            profileId == Guid.Empty)
        {
            return;
        }

        var existingPending = await _approvalRepository.GetRequestsByRequesterSidAsync(
            PatternCoverageRequester,
            patternConsent.TenantId,
            "PENDING",
            ct).ConfigureAwait(false);
        if (existingPending.Any(r =>
            r.TenantId == patternConsent.TenantId &&
            r.RequestKind == ConsentRequestKind.ProfileAssignment &&
            string.Equals(r.Status, "PENDING", StringComparison.OrdinalIgnoreCase) &&
            r.TableIdentifier == newTable.Identifier &&
            ProfileAssignmentSnapshots.Parse(r).ExtendingConsentId == patternConsent.Id))
        {
            return;
        }

        var itemNo = patternConsent.ProfileItemNo ?? 0;
        var objectPattern = patternConsent.ObjectPattern ?? newTable.Identifier.ToString();
        var context = new ProfileAssignmentRequestContext(
            profileId,
            "PatternCoverageExtension",
            MaskingPolicyMode.Default,
            patternConsent.RowScope,
            [new ProfileAssignmentSnapshotItem(itemNo, objectPattern, true, patternConsent.DataSource, patternConsent.WriteRules)],
            IncludesFutureObjects: true,
            ExtendingConsentId: patternConsent.Id,
            ExtendingItemNo: itemNo);

        var requestId = Guid.NewGuid();
        var requesterSid = PatternCoverageRequester;
        var justification = $"Pattern grant '{patternConsent.Id:D}' matched new object '{newTable.Identifier}' requiring owner approval.";
        var grantDecision = _grantPolicy.Evaluate(new GrantRequest(
            patternConsent.GranteeType,
            grantee.Value,
            GrantKind.Consent,
            patternConsent.ValidTo,
            now,
            GrantClassifier.Classify([newTable]),
            MaskingPolicyMode.Default,
            Requester: requesterSid.Value,
            Justification: justification));
        if (!grantDecision.Allowed && !string.Equals(grantDecision.ErrorCode, GrantErrorCodes.FourEyesRequired, StringComparison.Ordinal))
        {
            return;
        }

        var req = new ConsentRequest
        {
            Id = requestId,
            TableId = newTable.Table.Id,
            TableIdentifier = newTable.Identifier,
            RequesterSid = requesterSid,
            RequestedGranteeType = patternConsent.GranteeType,
            RequestedGranteeRef = grantee.Value,
            BusinessJustification = justification,
            Status = "PENDING",
            RequestedAt = now,
            RequestedValidFrom = now,
            RequestedValidTo = patternConsent.ValidTo,
            UpdatedAt = now,
            TenantId = patternConsent.TenantId,
            WorkflowMode = ConsentWorkflowMode.Internal,
            ContextJson = JsonSerializer.Serialize(context),
            RequestKind = ConsentRequestKind.ProfileAssignment,
            GrantKind = PermissionGrantKind.Data,
            ProfileId = profileId
        };

        var created = await _approvalRepository.CreateConsentRequestAsync(req, ct).ConfigureAwait(false);
        var rows = missingOwners.Select(owner => new ProfileAssignmentItemRow(
            patternConsent.TenantId,
            created.Id,
            itemNo,
            newTable.Identifier.ToString(),
            owner,
            "PENDING")).ToList();
        await _store.UpsertAssignmentItemsAsync(null, rows, ct).ConfigureAwait(false);
        await EmitEventAsync(created, ConsentEventTypes.RequestCreated, now, ct).ConfigureAwait(false);
    }

    public Task<int> RevokeAssignmentAsync(
        TenantId tenantId,
        Guid profileAssignmentId,
        Sid actorSid,
        string reason,
        CancellationToken ct = default) =>
        _materializer.RevokeAssignmentAsync(tenantId, profileAssignmentId, actorSid, reason, ct);

    private async Task EmitEventAsync(ConsentRequest request, string eventType, DateTimeOffset occurredAt, CancellationToken ct)
    {
        if (_eventOutbox is null)
        {
            return;
        }

        var payload = ConsentEventPayloads.ForRequest(request, occurredAt);
        await _eventOutbox.AppendAsync(
            new ConsentEventWrite(
                request.TenantId.Value,
                Guid.NewGuid(),
                eventType,
                request.Id.ToString("D"),
                ConsentEventPayloads.Serialize(payload),
                occurredAt,
                request.ExternalSystemId),
            transaction: null,
            ct: ct).ConfigureAwait(false);
    }
}
