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

    /// <summary>
    /// Identity of an approver independent of the actor prefix: the part after the last ':' (e.g. the ITSM-reported account in
    /// <c>ITSM_SNOW:instance:alice@corp</c>), or an explicitly passed ITSM account.
    /// </summary>
    public static string NormalizeApprover(string? value, string? itsmApproverAccount = null)
    {
        var candidate = string.IsNullOrWhiteSpace(itsmApproverAccount) ? (value ?? string.Empty) : itsmApproverAccount.Trim();
        var idx = candidate.LastIndexOf(':');
        return (idx >= 0 ? candidate[(idx + 1)..] : candidate).Trim();
    }

    /// <summary>
    /// Review R4-4 (rest): identifiers under which one person can appear (raw actor, account part, ITSM account) and that are
    /// resolved to a stable DATA_OWNERS id by the repositories, so that four-eyes does not depend on string spelling alone.
    /// </summary>
    public static IReadOnlyList<string> IdentifierCandidates(string? value, string? itsmApproverAccount = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                set.Add(v.Trim());
            }
        }

        Add(value);
        Add(NormalizeApprover(value));
        Add(itsmApproverAccount);
        if (!string.IsNullOrWhiteSpace(itsmApproverAccount))
        {
            Add(NormalizeApprover(value, itsmApproverAccount));
        }

        return set.ToList();
    }

    /// <summary>True when both identity sets resolve to at least one common data owner id (the same person).</summary>
    public static bool ShareOwnerId(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right) =>
        left.Count > 0 && right.Count > 0 && left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>True when the approver is the requester (by SID, by account part or by any known identifier).</summary>
    public static bool IsSelfApproval(ConsentRequest req, Sid approverSid, string? itsmApproverAccount = null)
    {
        ArgumentNullException.ThrowIfNull(req);

        var candidates = new List<string> { approverSid.Value ?? string.Empty, NormalizeApprover(approverSid.Value) };
        if (!string.IsNullOrWhiteSpace(itsmApproverAccount))
        {
            candidates.Add(itsmApproverAccount.Trim());
            candidates.Add(NormalizeApprover(approverSid.Value, itsmApproverAccount));
        }

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
    public static bool IsSameApprover(string storedApproverSid, Sid approverSid, string? itsmApproverAccount = null)
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

        return string.Equals(stored, NormalizeApprover(approverSid.Value, itsmApproverAccount), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stored, NormalizeApprover(approverSid.Value), StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureApprovableStatus(Guid requestId, string? status, bool isExternalItsmApproval)
    {
        bool isExternal = string.Equals(status, PendingExternal, StringComparison.OrdinalIgnoreCase);
        bool isInternal = string.Equals(status, Pending, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(status, PendingSecond, StringComparison.OrdinalIgnoreCase);

        if (isExternalItsmApproval ? !isExternal : !isInternal)
        {
            throw new InvalidOperationException(isExternal
                ? $"Request {requestId} waits for the external ITSM approval and can only be approved by the ITSM system."
                : $"Request {requestId} is in status '{status}' and cannot be approved.");
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
        Table.IsSensitivityHigh(sensitivity);

    public static bool RequiresFourEyesApproval(bool requiresFourEyes, string? sensitivity = null) =>
        requiresFourEyes || IsHighSensitivity(sensitivity);

    /// <summary>Review E-9: an ITSM-governed request stays with the change board for its second step as well.</summary>
    public static string StatusAfterApproval(bool requiresFourEyes, int stepNumber, bool isExternalItsmApproval, string? sensitivity = null) =>
        RequiresFourEyesApproval(requiresFourEyes, sensitivity) && stepNumber < 2
            ? (isExternalItsmApproval ? PendingExternal : PendingSecond)
            : Approved;

    public static bool IsItsmActor(Sid sid) => sid.Value.StartsWith("ITSM_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Review E-10: approval steps and rejections belong in the hash chain, not only in the mutable APPROVAL_STEPS table.</summary>
    public static AuditLogEntry BuildStepAudit(ConsentRequest req, Sid approver, string eventType, string decision, string? newStatus, string? itsmAccount, string? reason)
    {
        string? trimmedReason = reason is { Length: > 500 } ? reason[..500] : reason;
        return new AuditLogEntry
        {
            TenantId = req.TenantId,
            EventType = eventType,
            ActorSid = approver,
            TargetTable = req.TableIdentifier.ToString(),
            Decision = decision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                RequestId = req.Id,
                Requester = req.RequesterSid.Value,
                NewStatus = newStatus,
                ItsmApprover = itsmAccount,
                Reason = trimmedReason
            })
        };
    }
}
