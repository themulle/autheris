namespace Autheris.Application.ConsentRequests;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Authenticated decision transport envelope of an external workflow system. It deliberately carries no approver
/// capacity, no validity and no grant shape: the engine derives all of them from the request and the mapped approver.
/// </summary>
public sealed record ExternalDecisionEnvelope(
    Guid RequestId,
    int ExpectedVersion,
    Guid ExternalSystemId,
    string ExternalReference,
    bool Approve,
    AssertedApprover Approver,
    DateTimeOffset Timestamp,
    string Nonce,
    string Signature,
    string? Reason = null,
    ClaimsPrincipal? Caller = null);

/// <summary>Outcome of a successful evidence check: the realm-mapped approver and the evidence reference for the engine.</summary>
public sealed record ValidatedExternalDecision(SecurityPrincipalContext Approver, ExternalDecisionEvidence Evidence);

/// <summary>Validates the authenticity of external decision evidence. It never decides and never grants.</summary>
public interface IExternalDecisionGuard
{
    Task<ValidatedExternalDecision> ValidateAsync(
        ExternalDecisionEnvelope envelope,
        ConsentRequest request,
        ConsentWorkflowRoute? route,
        ExternalWorkflowSystem? system,
        CancellationToken ct = default);
}

/// <summary>Maps an asserted external approver to an Autheris principal through the identity realm; <see langword="null"/> when it cannot be mapped.</summary>
public interface IExternalApproverResolver
{
    Task<SecurityPrincipalContext?> ResolveAsync(TenantId tenant, AssertedApprover asserted, CancellationToken ct = default);
}

/// <summary>Decision entry point for external workflow transports: evidence validation, then the single engine decision path.</summary>
public interface IExternalDecisionService
{
    Task<PermissionDecisionResult> DecideAsync(ExternalDecisionEnvelope envelope, TenantId tenant, CancellationToken ct = default);
}
