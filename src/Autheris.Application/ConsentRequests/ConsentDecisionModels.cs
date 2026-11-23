namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;

/// <summary>
/// Stable governed error codes emitted by the consent decision pipeline and external decision guard.
/// </summary>
public static class ConsentDecisionErrorCodes
{
    public const string RequestNotFound = "CONSENT_REQUEST_NOT_FOUND";
    public const string PreconditionFailed = "PRECONDITION_FAILED";
    public const string WorkflowModeMismatch = "WORKFLOW_MODE_MISMATCH";
    public const string ApproverUnresolved = "APPROVER_UNRESOLVED";
    public const string SelfApprovalForbidden = "SELF_APPROVAL_FORBIDDEN";
    public const string ValidityExceedsRequested = "VALIDITY_EXCEEDS_REQUESTED";
    public const string ValidityExceedsMaximum = "VALIDITY_EXCEEDS_MAXIMUM";
    public const string RequestValidityElapsed = "REQUEST_VALIDITY_ELAPSED";
    public const string FourEyesSameApprover = "FOUR_EYES_SAME_APPROVER";
    public const string RequestNotDecidable = "CONSENT_REQUEST_NOT_DECIDABLE";
    public const string ExternalSystemNotAuthorized = "EXTERNAL_SYSTEM_NOT_AUTHORIZED";
    public const string DecisionNotPermitted = "DECISION_NOT_PERMITTED";
    public const string RerouteNotAllowed = "REROUTE_NOT_ALLOWED";
    public const string GrantNotFound = "GRANT_NOT_FOUND";
    public const string DecisionExceedsRequest = "DECISION_EXCEEDS_REQUEST";
}

public enum ConsentDecisionKind
{
    Approve,
    Reject
}

public sealed class ConsentDecisionException(string errorCode, string message, int statusCode = 400)
    : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Reads tenant-scoped workflow routes and registered external workflow systems.
/// </summary>
public interface IConsentWorkflowConfigRepository
{
    Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(
        TenantId tenantId,
        TableIdentifier table,
        CancellationToken ct = default);

    Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(
        TenantId tenantId,
        Guid systemId,
        CancellationToken ct = default);

    Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemByClientIdAsync(
        TenantId tenantId,
        string clientId,
        CancellationToken ct = default)
        => Task.FromResult<ExternalWorkflowSystem?>(null);
}
