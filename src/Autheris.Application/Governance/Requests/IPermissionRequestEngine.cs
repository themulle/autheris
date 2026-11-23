using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Single entry point for submitting, withdrawing and deciding permission requests. REST, GraphQL, MCP, bundle,
/// discovery and external-decision callers use this abstraction and never write requests or grants themselves.
/// </summary>
public interface IPermissionRequestEngine
{
    Task<PermissionSubmitResult> SubmitAsync(PermissionRequestSubmission submission, CancellationToken ct = default);

    Task<PermissionSubmitResult> SubmitBundleAsync(
        ClaimsPrincipal caller,
        TenantId tenantId,
        IReadOnlyList<PermissionRequestSubmission> items,
        SecurityPrincipalContext? callerContext = null,
        CancellationToken ct = default);

    Task<PermissionDecisionResult> DecideAsync(PermissionDecisionCommand command, CancellationToken ct = default);

    /// <summary>Withdraws a pending request. Only the requester can withdraw; every other caller sees the request as not found.</summary>
    Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal caller, CancellationToken ct = default);
}
