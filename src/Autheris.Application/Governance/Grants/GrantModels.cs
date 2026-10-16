namespace Autheris.Application.Governance.Grants;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>Every path that writes a grant. Used for classification, audit and the architecture test.</summary>
public enum GrantKind
{
    Consent,
    AccessProfileAssignment,
    VirtualFilterProfile,
    ManifestPolicy,
    McpAdminGrant,
    BulkGrant,
    Renewal,
    BreakGlass,
    RebacTuple
}

/// <summary>Sensitivity class of the matched tables and columns.</summary>
public enum GrantSensitivity
{
    Regular = 0,
    Sensitive = 1
}

/// <summary>Resulting class of a grant; selects the maximum duration and the recertification cadence.</summary>
public enum GrantClass
{
    /// <summary>Group/role grantee: not an individual grant, not limited by this policy.</summary>
    Standing,
    Regular,
    Sensitive,
    IndividualUnmasked,
    IndividualPrivileged,
    BreakGlass,
    ServicePrincipal,
    CustomerRegular
}

public enum GrantRecertClass
{
    None,
    Campaign365,
    Campaign90,
    CustomerAdminAttestation
}

/// <param name="Approvers">
/// Null when the grant is only being validated before approval (request time); a list (possibly empty) when approvals are being
/// enforced at activation time. Four-eyes is enforced only when this is not null.
/// </param>
/// <param name="Effect">A <see cref="ConsentEffect.Deny"/> only restricts access: it is never limited (no expiry, no maximum) and never clamped, because an expiring deny would fail open.</param>
/// <param name="Renewer">The actor of a renewal. A renewal by the grantee is always rejected.</param>
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
    ConsentEffect Effect = ConsentEffect.Allow);

/// <param name="ErrorCode">Stable machine code when <see cref="Allowed"/> is false (HTTP 400/403 mapping by the caller).</param>
public sealed record GrantDecision(
    bool Allowed,
    DateTimeOffset? MaxValidTo,
    bool RequiresFourEyes,
    GrantRecertClass RecertClass,
    GrantClass Class,
    string Reason,
    string? ErrorCode = null,
    bool SuggestRoleConversion = false,
    bool RequiresCampaignDecision = false)
{
    public static GrantDecision Deny(string errorCode, string reason, GrantClass cls = GrantClass.Regular, DateTimeOffset? maxValidTo = null) =>
        new(false, maxValidTo, true, GrantRecertClass.Campaign90, cls, reason, errorCode);
}

public static class GrantErrorCodes
{
    public const string RequiresExpiry = "INDIVIDUAL_GRANT_REQUIRES_EXPIRY";
    public const string ExpiryInPast = "INDIVIDUAL_GRANT_EXPIRY_IN_PAST";
    public const string ExceedsMaximum = "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM";
    public const string FourEyesRequired = "INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES";
    public const string RenewalByGrantee = "INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE";
    public const string RenewalRequiresCampaign = "INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN";
    public const string DisabledForRealm = "INDIVIDUAL_GRANT_DISABLED_FOR_REALM";
    public const string JustificationRequired = "INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION";
    public const string Indeterminate = "INDIVIDUAL_GRANT_INDETERMINATE";
}

/// <summary>Single decision point for every grant write path (WP-SC-16, SEC-SC-20). Fail closed on any error.</summary>
public interface IGrantPolicy
{
    GrantDecision Evaluate(GrantRequest request);
}

/// <summary>Thrown by write paths when <see cref="IGrantPolicy"/> denies a grant. Endpoints map it to HTTP 400 with <see cref="ErrorCode"/>.</summary>
public sealed class GrantPolicyViolationException(GrantDecision decision)
    : InvalidOperationException(decision.Reason)
{
    public string ErrorCode { get; } = decision.ErrorCode ?? GrantErrorCodes.Indeterminate;

    public DateTimeOffset? MaxValidTo { get; } = decision.MaxValidTo;

    public GrantDecision Decision { get; } = decision;

    /// <summary>Returns when allowed, otherwise throws.</summary>
    public static void ThrowIfDenied(GrantDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Allowed)
        {
            throw new GrantPolicyViolationException(decision);
        }
    }
}
