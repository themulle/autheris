namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Domain.Security;

/// <summary>
/// Canonical external API application roles and delegated scopes for consent and catalog operations.
/// </summary>
public static class ExternalApiPermissions
{
    public const string CatalogRead = "Autheris.Catalog.Read";
    public const string CatalogReadAdmin = "Autheris.Catalog.Read.Admin";
    public const string ConsentRead = "Autheris.Consent.Read";
    public const string ConsentDecide = "Autheris.Consent.Decide";
    public const string GrantsRevoke = "Autheris.Grants.Revoke";
    public const string GrantsVerify = "Autheris.Grants.Verify";

    private static readonly string[] PersonIdClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectSid",
        "onprem_sid",
        "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        ClaimTypes.Upn,
        "upn",
        "preferred_username",
        ClaimTypes.Email,
        "email"
    ];

    private static readonly string[] InteractiveReadConsentRoles =
    [
        "Consumer", "Developer", "AiAgent", "McpCaller",
        "Operator", "DataSteward", "DataOwner", "SchemaAdmin",
        "GovernanceAdmin", "TenantAdmin", "SecurityAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin",
        "SecurityAuditor", "Auditor", "PrivacyAdmin", "DataProtectionOfficer"
    ];

    public static bool IsServicePrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var gty = principal.FindFirst("gty")?.Value;
        if (string.Equals(gty, "client-credentials", StringComparison.OrdinalIgnoreCase)
            || string.Equals(gty, "client_credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var preferred = principal.FindFirst("preferred_username")?.Value;
        if (preferred?.StartsWith("service-account-", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(GetClientId(principal))
            && TokenAccessScope.GetScopeSet(principal.Claims).Count == 0
            && !HasAnyClaim(principal, PersonIdClaimTypes))
        {
            return true;
        }

        return false;
    }

    public static string? GetClientId(ClaimsPrincipal? principal) =>
        principal?.FindFirst("azp")?.Value
        ?? principal?.FindFirst("appid")?.Value
        ?? principal?.FindFirst("client_id")?.Value;

    public static bool HasAppRole(ClaimsPrincipal? principal, string permission)
    {
        if (principal is null || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        foreach (var claim in principal.Claims)
        {
            if (claim.Type is "roles" or "role" or ClaimTypes.Role)
            {
                foreach (var token in SplitClaimTokens(claim.Value))
                {
                    if (string.Equals(token, permission, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return principal.IsInRole(permission);
    }

    public static bool HasDelegatedScope(ClaimsPrincipal? principal, string permission)
    {
        if (principal is null || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        return TokenAccessScope.GetScopeSet(principal.Claims).Contains(permission);
    }

    public static bool HasAnyExternalScope(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return false;
        }

        foreach (var scope in TokenAccessScope.GetScopeSet(principal.Claims))
        {
            if (scope.StartsWith("Autheris.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool AllowsCatalogRead(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (HasAppRole(principal, CatalogRead) || HasAppRole(principal, CatalogReadAdmin)
            || HasDelegatedScope(principal, CatalogRead) || HasDelegatedScope(principal, CatalogReadAdmin))
        {
            return true;
        }

        // Interactive person tokens may read; discovery decides what each caller sees.
        return !IsServicePrincipal(principal) && !HasAnyExternalScope(principal);
    }

    public static bool AllowsCatalogReadAdmin(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (IsServicePrincipal(principal))
        {
            return HasAppRole(principal, CatalogReadAdmin);
        }

        return HasAppRole(principal, CatalogReadAdmin)
            || HasDelegatedScope(principal, CatalogReadAdmin)
            || (!HasAnyExternalScope(principal) && GatewayPolicies.HasAnyRole(principal, GatewayPolicies.AdminOnlyRoles));
    }

    public static bool AllowsConsentRead(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (IsServicePrincipal(principal))
        {
            return HasAppRole(principal, ConsentRead);
        }

        if (HasAppRole(principal, ConsentRead) || HasDelegatedScope(principal, ConsentRead))
        {
            return true;
        }

        if (HasAnyExternalScope(principal))
        {
            return GatewayPolicies.HasAnyRole(principal, GatewayPolicies.AdminOnlyRoles)
                || GatewayPolicies.HasAnyRole(principal, GatewayPolicies.ApproverRoles);
        }

        return GatewayPolicies.HasAnyRole(principal, InteractiveReadConsentRoles)
            || HasAnyClaim(principal, PersonIdClaimTypes);
    }

    public static bool AllowsGrantVerify(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (IsServicePrincipal(principal))
        {
            return HasAppRole(principal, GrantsVerify) || HasAppRole(principal, ConsentRead);
        }

        return HasAppRole(principal, GrantsVerify)
            || HasDelegatedScope(principal, GrantsVerify)
            || (!HasAnyExternalScope(principal)
                && (GatewayPolicies.HasAnyRole(principal, GatewayPolicies.AuditorOrAboveRoles)
                    || GatewayPolicies.HasAnyRole(principal, GatewayPolicies.AdminOnlyRoles)));
    }

    private static bool HasAnyClaim(ClaimsPrincipal principal, IEnumerable<string> claimTypes)
    {
        foreach (var type in claimTypes)
        {
            if (!string.IsNullOrWhiteSpace(principal.FindFirst(type)?.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitClaimTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }
}
