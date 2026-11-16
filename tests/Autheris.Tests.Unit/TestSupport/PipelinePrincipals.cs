namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Identity;

/// <summary>
/// Test-only helper that stamps a <see cref="ClaimsPrincipal"/> with pipeline group-resolution markers
/// so unit tests that exercise downstream PDP paths can construct <see cref="GroupResolutionStatus.Complete"/> subjects.
/// </summary>
internal static class PipelinePrincipals
{
    public static ClaimsPrincipal MarkComplete(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var clone = new ClaimsPrincipal();
        foreach (var identity in principal.Identities)
        {
            var filtered = new ClaimsIdentity(
                identity.Claims.Where(c =>
                    !string.Equals(c.Type, GroupResolutionClaims.StatusClaimType, StringComparison.Ordinal)
                    && !string.Equals(c.Type, GroupResolutionClaims.MarkerClaimType, StringComparison.Ordinal)),
                identity.AuthenticationType,
                identity.NameClaimType,
                identity.RoleClaimType);
            clone.AddIdentity(filtered);
        }

        clone.AddIdentity(new ClaimsIdentity(
            [GroupResolutionClaims.CreateMarker(), GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)],
            principal.Identity?.AuthenticationType));

        return clone;
    }
}
