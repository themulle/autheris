using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Governance.Requests;

/// <summary>
/// Submission command accepted by <see cref="PermissionRequestEngine"/> across REST, GraphQL, MCP, bundle, and internal callers.
/// Never carries a caller-asserted self-grant flag.
/// </summary>
public sealed record PermissionRequestSubmission(
    ClaimsPrincipal Caller,
    TenantId TenantId,
    ConsentRequestKind RequestKind,
    PermissionTarget Target,
    GranteeType GranteeType,
    string GranteeRef,
    string? Justification,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidTo = null,
    int? DurationDays = null,
    string? RequestedDuration = null,
    IReadOnlyList<WriteRule>? RequestedWriteRules = null,
    bool IncludeReadAccess = false,
    Guid? BundleId = null,
    SecurityPrincipalContext? CallerContext = null);

/// <param name="AutoApproved">True only when the Development-only insecure auto-approve switch activated the grant at submission.</param>
public sealed record PermissionSubmitResult(
    ConsentRequest Request,
    IReadOnlyList<ConsentRequest>? BundleRequests = null,
    Guid? BundleId = null,
    bool AutoApproved = false);

/// <summary>
/// Authenticated external workflow decision reference attached to a <see cref="PermissionDecisionCommand"/>.
/// Capacity and validity are never read from external claims.
/// </summary>
public sealed record ExternalDecisionEvidence(
    Guid ExternalSystemId,
    string ExternalReference,
    string? Justification = null);

/// <summary>
/// Single-decision command evaluated by <see cref="PermissionRequestEngine"/>.
/// Self-grant status is computed strictly internally from SoD facts and is absent from external DTOs.
/// </summary>
public sealed record PermissionDecisionCommand(
    Guid RequestId,
    int ExpectedVersion,
    SecurityPrincipalContext Decider,
    bool Approve,
    DateTimeOffset? ValidTo = null,
    string? Reason = null,
    bool AcknowledgeCoveredStricterColumns = false,
    ExternalDecisionEvidence? External = null);

/// <summary>
/// Persisted CAS transition metadata committed together with grant materialization.
/// </summary>
public sealed record PermissionDecisionRecord(
    string NewStatus,
    Sid DecidedBy,
    DateTimeOffset DecidedAt,
    ApproverCapacity Capacity,
    bool SelfGrant,
    string StepDecision,
    string? Reason,
    DateTimeOffset ApprovedValidTo);

public enum DecisionCasOutcome
{
    Applied = 0,
    NotFound = 1,
    AlreadyDecided = 2
}

public sealed record GrantRef(
    Guid GrantId,
    PermissionGrantKind GrantKind,
    TableIdentifier? Table = null,
    ContainerKind? ContainerKind = null,
    Guid? ContainerId = null,
    DateTimeOffset ValidFrom = default,
    DateTimeOffset? ValidTo = null,
    long GrantRecordSeq = 0);

public sealed record DecisionCasResult(
    DecisionCasOutcome Outcome,
    ConsentRequest? Request = null,
    IReadOnlyList<GrantRef>? CreatedGrants = null);

public sealed record PermissionDecisionResult(
    ConsentRequest Request,
    IReadOnlyList<GrantRef> Grants,
    GrantRef? PrimaryGrant = null);

/// <summary>
/// Renewal command evaluated by <see cref="PermissionRequestEngine"/> or grant lifecycle callers.
/// Renewals require a fresh justification, measure validity from renewal time against decider capacity,
/// and forbid self-renewal by the grantee.
/// </summary>
public sealed record GrantRenewalCommand(
    Guid ConsentId,
    SecurityPrincipalContext Renewer,
    DateTimeOffset? NewValidTo,
    string? Justification);

/// <summary>Outcome of a committed renewal: the new consent (signed <c>Renewed</c> ledger entry) and the consent it replaced.</summary>
public sealed record GrantRenewalResult(Consent Renewed, Guid PreviousConsentId, ApproverCapacity Capacity);
