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
}

public enum ConsentDecisionKind
{
    Approve,
    Reject
}

public enum ConsentDecisionChannel
{
    Internal,
    External
}

public sealed record ConsentDecisionCommand(
    Guid RequestId,
    ConsentDecisionKind Decision,
    ConsentDecisionChannel Channel,
    ClaimsPrincipal? Principal,
    int? ExpectedVersion = null,
    DateTimeOffset? ApprovedValidTo = null,
    string? Reason = null,
    string? Description = null,
    IReadOnlyList<WriteRule>? WriteRules = null,
    string? ColumnMaskingJson = null,
    string? RowFilterJson = null,
    string? IdempotencyKey = null,
    ExternalDecisionAttribution? ExternalAttribution = null,
    TenantId? ContextTenantId = null,
    IReadOnlyList<ConsentRowFilter>? PrebuiltRowFilters = null,
    IReadOnlyList<ConsentColumnRule>? PrebuiltColumnRules = null,
    bool HasPrebuiltRestrictions = false);

public sealed record ConsentDecisionResult(
    ConsentRequest Request,
    Consent? ActivatedConsent = null);

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
}
