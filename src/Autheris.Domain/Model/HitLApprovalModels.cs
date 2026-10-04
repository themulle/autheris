namespace Autheris.Domain.Model;

using System;
using Autheris.Domain.Common;

public enum HitLApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Expired = 4
}

public sealed record HitLApprovalTicket(
    string ApprovalId,
    string ToolName,
    string TenantId,
    string RequesterSid,
    TableIdentifier TargetTable,
    string? Justification,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    HitLApprovalStatus Status,
    string? ApproverSid = null,
    string? RejectionReason = null,
    string? ItsmTicketId = null,
    string? ItsmTicketUrl = null
);

public sealed record HitLApprovalResult(
    bool IsApproved,
    HitLApprovalTicket Ticket,
    string? Message = null
);

public sealed record HitLApprovalBroadcast(
    string ApprovalId,
    string ApproverSid,
    bool IsApproved,
    string? Reason = null
);
