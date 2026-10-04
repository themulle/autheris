namespace Autheris.Application.Mcp.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IHitLStepUpApprovalService
{
    Task<HitLApprovalResult> RequestStepUpApprovalAsync(
        string toolName,
        string tenantId,
        string requesterSid,
        TableIdentifier targetTable,
        string? justification = null,
        CancellationToken ct = default);

    HitLApprovalResult ApproveStepUpRequest(string approvalId, string approverSid);

    HitLApprovalResult RejectStepUpRequest(string approvalId, string approverSid, string? reason = null);

    /// <summary>
    /// SEC C-05: Approves a ticket with full approver context (all identifiers + tenant binding).
    /// </summary>
    HitLApprovalResult ApproveStepUpRequest(string approvalId, HitLApproverContext approver);

    /// <summary>
    /// SEC C-05: Rejects a ticket with full approver context (tenant binding).
    /// </summary>
    HitLApprovalResult RejectStepUpRequest(string approvalId, HitLApproverContext approver, string? reason = null);

    HitLApprovalTicket? GetTicket(string approvalId);

    IReadOnlyList<HitLApprovalTicket> GetPendingTickets(string? tenantId = null);
}

/// <summary>
/// SEC C-05: Identity of an approver for HitL Four-Eyes decisions.
/// </summary>
/// <param name="ApproverSid">Primary approver identity (same derivation as the requester: <c>GetUserSid()</c>).</param>
/// <param name="Identifiers">All user-bound identifiers of the approver (oid, sub, upn, NameIdentifier, PrimarySid ...).
/// If any of them equals the requester identity the approval is treated as self-approval.</param>
/// <param name="TenantId">Tenant of the approver. Tickets of other tenants are invisible unless <paramref name="IsCrossTenantAdmin"/> is set.</param>
/// <param name="IsCrossTenantAdmin">True for cluster-wide administrators that may act across tenants.</param>
public sealed record HitLApproverContext(
    string ApproverSid,
    IReadOnlyCollection<string> Identifiers,
    string? TenantId,
    bool IsCrossTenantAdmin = false);
