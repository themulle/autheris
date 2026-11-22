namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>Every path that writes a grant. Used for classification, audit and the architecture test.</summary>
public enum GrantKind
{
    Consent,
    AccessProfileAssignment,
    VirtualFilterProfile,
    ManifestPolicy,
    McpAdminGrant,
    BulkGrant,
    Renewal
}

/// <summary>Sensitivity class of the matched tables and columns.</summary>
public enum GrantSensitivity
{
    Regular = 0,
    Sensitive = 1
}

/// <summary>Resulting capacity/standing class of a grant; determines maximum duration.</summary>
public enum GrantClass
{
    /// <summary>Group/role deny or legacy standing grant.</summary>
    Standing,
    DataOwner,
    Governance,
    ItAdmin,
    SelfGrant
}

/// <summary>
/// Input to <see cref="IGrantPolicy"/> evaluation across legacy and capacity-based permission flows.
/// </summary>
public sealed record GrantRequest(
    GranteeType GranteeType,
    string GranteeRef,
    GrantKind Kind,
    DateTimeOffset? ValidTo,
    DateTimeOffset Now,
    GrantSensitivity Sensitivity = GrantSensitivity.Regular,
    MaskingPolicyMode MaskingMode = MaskingPolicyMode.Default,
    bool PrivilegedRight = false,
    bool CustomerRealm = false,
    string? Requester = null,
    IReadOnlyList<string>? Approvers = null,
    string? Renewer = null,
    int RenewalCount = 0,
    string? Justification = null,
    ConsentEffect Effect = ConsentEffect.Allow,
    ConsentRequestKind? RequestKind = null,
    ApproverCapacity? Capacity = null,
    bool SelfGrant = false,
    ProtectionClass ProtectionClass = ProtectionClass.Confidential,
    TenantAccessPolicy? TenantPolicy = null);

/// <param name="ErrorCode">Stable machine code when <see cref="Allowed"/> is false (HTTP 400/422/403 mapping by the caller).</param>
public sealed record GrantDecision(
    bool Allowed,
    DateTimeOffset? MaxValidTo,
    bool RequiresFourEyes,
    GrantClass Class,
    string Reason,
    string? ErrorCode = null,
    bool SuggestRoleConversion = false,
    bool RequiresCampaignDecision = false)
{
    public static GrantDecision Deny(string errorCode, string reason, GrantClass cls = GrantClass.DataOwner, DateTimeOffset? maxValidTo = null) =>
        new(false, maxValidTo, false, cls, reason, errorCode);
}

public static class GrantErrorCodes
{
    public const string RequiresExpiry = "INDIVIDUAL_GRANT_REQUIRES_EXPIRY";
    public const string EndDateRequired = "GRANT_END_DATE_REQUIRED";
    public const string ExpiryInPast = "INDIVIDUAL_GRANT_EXPIRY_IN_PAST";
    public const string ExceedsMaximum = "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM";
    public const string ExceedsApproverLimit = "GRANT_DURATION_EXCEEDS_APPROVER_LIMIT";
    public const string FourEyesRequired = "INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES";
    public const string RenewalByGrantee = "INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE";
    public const string RenewalRequiresCampaign = "INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN";
    public const string DisabledForRealm = "INDIVIDUAL_GRANT_DISABLED_FOR_REALM";
    public const string JustificationRequired = "INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION";
    public const string SelfGrantSecretRefused = "SELF_GRANT_SECRET_REFUSED";
    public const string SelfGrantExceedsCeiling = "SELF_GRANT_EXCEEDS_CEILING";
    public const string Indeterminate = "INDIVIDUAL_GRANT_INDETERMINATE";
}

/// <summary>Single decision point for every grant write path. Fail closed on any error.</summary>
public interface IGrantPolicy
{
    GrantDecision Evaluate(GrantRequest request);
}

/// <summary>Thrown by write paths when <see cref="IGrantPolicy"/> denies a grant. Endpoints map it to HTTP 400/422 with <see cref="ErrorCode"/>.</summary>
public sealed class GrantPolicyViolationException(GrantDecision decision)
    : InvalidOperationException(decision.Reason)
{
    public string ErrorCode { get; } = decision.ErrorCode ?? GrantErrorCodes.Indeterminate;
    public DateTimeOffset? MaxValidTo { get; } = decision.MaxValidTo;
    public GrantDecision Decision { get; } = decision;

    public static void ThrowIfDenied(GrantDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Allowed)
        {
            throw new GrantPolicyViolationException(decision);
        }
    }
}
