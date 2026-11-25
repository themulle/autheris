using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Review PG-3/R4-4: provider-independent rules of the consent approval workflow (separation of duties, four-eyes,
/// status transitions). Both governance repositories use this class so their behaviour cannot drift apart.
/// </summary>
internal static class ConsentApprovalPolicy
{
    public const string Pending = "PENDING";
    public const string PendingSecond = "PENDING_SECOND_APPROVAL";
    public const string PendingExternal = "PENDING_EXTERNAL_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Withdrawn = "WITHDRAWN";
    public const string RequestStatusWithdrawn = "WITHDRAWN";

    /// <summary>The approver already recorded a step of this request (four-eyes requires two different people).</summary>
    public const string SameApproverViolationMessage = "FOUR_EYES_SAME_APPROVER: Four-eyes principle violated; this approver has already approved this request.";

    /// <summary>
    /// Security review (four-eyes on the atomic path): the final approval with consent creation was called for a step that does not
    /// complete the four-eyes requirement (e.g. the first step of a four-eyes request). Nothing is written.
    /// </summary>
    public static string FourEyesNotSatisfiedMessage(Guid requestId) =>
        $"Four-eyes principle violated: Request {requestId} requires a second, independent approval before it can be approved and the consent created.";

    /// <summary>
    /// Canonical identity of an approver normalized by trimming and extracting any trailing identity segment after ':'.
    /// </summary>
    public static string NormalizeApprover(string? value)
    {
        var candidate = value ?? string.Empty;
        var idx = candidate.LastIndexOf(':', StringComparison.Ordinal);
        return (idx >= 0 ? candidate[(idx + 1)..] : candidate).Trim();
    }

    /// <summary>True when the approver is the requester (by SID, normalized identifier, or any known identifier).</summary>
    public static bool IsSelfApproval(ConsentRequest req, Sid approverSid)
    {
        ArgumentNullException.ThrowIfNull(req);

        var candidates = new List<string> { approverSid.Value ?? string.Empty, NormalizeApprover(approverSid.Value) };
        var requesterIds = new List<string> { req.RequesterSid.Value };
        if (req.RequesterIdentifiers != null)
        {
            requesterIds.AddRange(req.RequesterIdentifiers.Where(i => !string.IsNullOrWhiteSpace(i)));
        }

        // Empty candidates (e.g. an actor ending in ':') never match anything.
        return requesterIds.Any(r => !string.IsNullOrWhiteSpace(r) &&
                                     candidates.Any(c => !string.IsNullOrWhiteSpace(c) && string.Equals(r, c, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>True when a recorded approval step (stored approver SID) was made by the same person as the current approver.</summary>
    public static bool IsSameApprover(string storedApproverSid, Sid approverSid)
    {
        if (!string.IsNullOrWhiteSpace(storedApproverSid) &&
            string.Equals(storedApproverSid, approverSid.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stored = NormalizeApprover(storedApproverSid);
        if (stored.Length == 0)
        {
            return false;
        }

        return string.Equals(stored, NormalizeApprover(approverSid.Value), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Guarded status and version transition for consent request decision steps on every database engine.
    /// </summary>
    public const string GuardedStatusUpdateSql = "UPDATE CONSENT_REQUESTS SET status = @st, version = version + 1, updated_at = @updatedAt WHERE id = @id AND status = @expected";

    /// <summary>Zero affected rows: the caller rolls the transaction back (step, audit, status) and the decision answers ACCESS_REQUEST_NOT_PENDING.</summary>
    public static void EnsureTransitionApplied(int affectedRows, Guid requestId)
    {
        if (affectedRows != 1)
        {
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
        }
    }

    public static void EnsureApprovableStatus(Guid requestId, string? status, ConsentWorkflowMode channelMode = ConsentWorkflowMode.Internal)
    {
        bool isExternalStatus = string.Equals(status, PendingExternal, StringComparison.OrdinalIgnoreCase);
        bool isInternalStatus = string.Equals(status, Pending, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(status, PendingSecond, StringComparison.OrdinalIgnoreCase);

        if (channelMode == ConsentWorkflowMode.External ? !isExternalStatus : !isInternalStatus)
        {
            if (isExternalStatus && channelMode == ConsentWorkflowMode.Internal)
            {
                throw new InvalidOperationException($"Request {requestId} is in external workflow mode and cannot be approved through internal channels.");
            }

            if (isInternalStatus && channelMode == ConsentWorkflowMode.External)
            {
                throw new InvalidOperationException($"Request {requestId} is in internal workflow mode and cannot be approved through external channels.");
            }

            // The request left the approvable states (a concurrent decision won): the loser's outcome is ACCESS_REQUEST_NOT_PENDING.
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
        }
    }

    public static bool IsRejectableStatus(string? status) =>
        string.Equals(status, Pending, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, PendingSecond, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, PendingExternal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ADR-008: HIGH, RESTRICTED, and SECRET sensitivity classifications mandate four-eyes approval by default.
    /// </summary>
    public static bool IsHighSensitivity(string? sensitivity) =>
        string.Equals(sensitivity, "HIGH", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sensitivity, "RESTRICTED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(sensitivity, "SECRET", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresFourEyesApproval(bool requiresFourEyes, string? sensitivity = null) =>
        requiresFourEyes || IsHighSensitivity(sensitivity);

    public static string StatusAfterApproval(bool requiresFourEyes, int stepNumber, ConsentWorkflowMode mode = ConsentWorkflowMode.Internal, string? sensitivity = null) =>
        RequiresFourEyesApproval(requiresFourEyes, sensitivity) && stepNumber < 2
            ? (mode == ConsentWorkflowMode.External ? PendingExternal : PendingSecond)
            : Approved;

    public static AuditLogEntry BuildStepAudit(
        ConsentRequest req,
        Sid approver,
        string eventType,
        string decision,
        string? newStatus,
        string? reason,
        string? externalReference = null,
        string? correlationId = null)
    {
        string? trimmedReason = reason is { Length: > 500 } ? reason[..500] : reason;
        return new AuditLogEntry
        {
            TenantId = req.TenantId,
            EventType = eventType,
            ActorSid = approver,
            TargetTable = req.TableIdentifier.ToString(),
            Decision = decision,
            TraceId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                RequestId = req.Id,
                Requester = req.RequesterSid.Value,
                NewStatus = newStatus,
                ExternalReference = externalReference,
                Reason = trimmedReason
            })
        };
    }
}
