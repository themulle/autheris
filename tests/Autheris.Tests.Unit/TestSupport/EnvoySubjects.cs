namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Security.Claims;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

/// <summary>Builds the request security context the Envoy endpoint hands to the ext_authz service, like the request pipeline does.</summary>
internal static class EnvoySubjects
{
    /// <summary>Null stays null (no request context), an unauthenticated principal is anonymous, an authenticated one gets a resolved subject.</summary>
    public static SecurityPrincipalContext? From(ClaimsPrincipal? principal, GroupResolutionStatus status = GroupResolutionStatus.Complete)
    {
        if (principal is null)
        {
            return null;
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return SecurityPrincipalContext.Anonymous();
        }

        // The request factory rejects an authenticated principal without a SID (no display-name fallback).
        _ = principal.GetUserSid() ?? throw new UnauthorizedAccessException("Authenticated principal lacks a valid SID claim.");
        return SecurityPrincipalContext.FromPrincipal(principal) with { GroupResolutionStatus = status };
    }
}
