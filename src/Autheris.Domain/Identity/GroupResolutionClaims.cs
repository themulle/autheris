namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using Autheris.Domain.Common;

/// <summary>Whether the group set of a caller is complete, or unknown and therefore never to be treated as "no groups".</summary>
public enum GroupResolutionStatus
{
    Complete = 1,
    Degraded = 2
}

/// <summary>
/// Claims minted by the identity pipeline only (issuer <see cref="IdentityClaimIssuers.Pipeline"/>): the processing marker, the group
/// resolution status and the instant after which the resolution must be repeated. A claim of the same type with another issuer is never trusted.
/// </summary>
public static class GroupResolutionClaims
{
    public const string MarkerClaimType = "autheris:identity_pipeline";
    public const string StatusClaimType = "autheris:group_resolution";
    public const string ExpiresAtClaimType = "autheris:group_resolution_expires";
    public const string CompleteValue = "complete";
    public const string DegradedValue = "degraded";

    /// <summary>
    /// Every inbound claim type that may carry group values (token, Kerberos PAC, WS-Federation). The identity pipeline strips them; the group
    /// resolver is the only reader. Group SIDs for authorization come from pipeline-issuer <c>GroupSid</c> claims only.
    /// </summary>
    public static readonly IReadOnlyList<string> InboundGroupClaimTypes =
    [
        ClaimTypes.GroupSid,
        "groups",
        "groupsid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid"
    ];

    public static Claim CreateMarker() => Pipeline(MarkerClaimType, "1");

    public static Claim CreateStatus(GroupResolutionStatus status) =>
        Pipeline(StatusClaimType, status == GroupResolutionStatus.Complete ? CompleteValue : DegradedValue);

    public static Claim CreateExpiresAt(DateTimeOffset expiresAt) =>
        Pipeline(ExpiresAtClaimType, expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>True when the principal carries the pipeline marker minted by the gateway itself.</summary>
    public static bool HasPipelineMarker(ClaimsPrincipal? principal) =>
        principal?.FindFirst(c => c.Type == MarkerClaimType && IsPipeline(c)) is not null;

    /// <summary>
    /// The status of a principal: Complete only for a pipeline-marked principal with a pipeline status claim of "complete" and without the
    /// role-resolution degraded marker. A principal that did not pass the pipeline is Degraded.
    /// </summary>
    public static GroupResolutionStatus Read(ClaimsPrincipal? principal)
    {
        if (principal is null || !HasPipelineMarker(principal) || IsMarkedDegraded(principal))
        {
            return GroupResolutionStatus.Degraded;
        }

        var status = principal.FindFirst(c => c.Type == StatusClaimType && IsPipeline(c));
        return status is not null && string.Equals(status.Value, CompleteValue, StringComparison.Ordinal)
            ? GroupResolutionStatus.Complete
            : GroupResolutionStatus.Degraded;
    }

    /// <summary>The instant after which the group resolution of a pipeline-processed principal is stale; null when absent.</summary>
    public static DateTimeOffset? ReadExpiresAt(ClaimsPrincipal? principal)
    {
        var claim = principal?.FindFirst(c => c.Type == ExpiresAtClaimType && IsPipeline(c));
        return claim is not null && long.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The role-resolution marker (any issuer: it only ever restricts) or a degraded pipeline status.</summary>
    public static bool IsMarkedDegraded(ClaimsPrincipal? principal) =>
        principal?.HasClaim(c =>
            (string.Equals(c.Type, RoleResolutionClaims.ClaimType, StringComparison.OrdinalIgnoreCase)
             && string.Equals(c.Value, RoleResolutionClaims.Degraded, StringComparison.OrdinalIgnoreCase))
            || (c.Type == StatusClaimType && IsPipeline(c) && c.Value == DegradedValue)) == true;

    private static bool IsPipeline(Claim claim) => string.Equals(claim.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal);

    private static Claim Pipeline(string type, string value) => new(type, value, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline);
}
