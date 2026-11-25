using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Review PG-3/R4-4: provider-independent rules of the legacy consent rejection workflow (status transitions, step audit). Both
/// governance engines use this class so their behaviour cannot drift apart. Approvals, including the four-eyes review of grants on
/// <c>secret</c> data, run only through the permission request engine (<c>SecretFourEyesRule</c>, issue #1132).
/// </summary>
internal static class ConsentApprovalPolicy
{
    public const string Pending = "PENDING";
    public const string PendingSecond = "PENDING_SECOND_APPROVAL";
    public const string PendingExternal = "PENDING_EXTERNAL_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Withdrawn = "WITHDRAWN";
    public const string RequestStatusWithdrawn = "WITHDRAWN";

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

    public static bool IsRejectableStatus(string? status) =>
        string.Equals(status, Pending, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, PendingSecond, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, PendingExternal, StringComparison.OrdinalIgnoreCase);

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
