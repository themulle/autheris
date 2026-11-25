using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Catalog.External;
using Autheris.Application.Identity;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

namespace Autheris.Application.Policy.Protection;

/// <summary>
/// Determines whether an authenticated caller satisfies the tenant's internal-audience definition
/// through verified Entra workforce tenant binding or membership in an employee group.
/// </summary>
public static class InternalAudienceResolver
{
    private static readonly string[] EntraHosts =
    [
        "login.microsoftonline.com",
        "sts.windows.net",
        "login.microsoftonline.us",
        "login.partner.microsoftonline.cn"
    ];

    public static bool IsInternal(
        SecurityPrincipalContext? subject,
        InternalAudiencePolicy? criterion,
        IIdentityRealmRegistry? realmRegistry = null,
        IdentityRealm? realm = null,
        TimeProvider? timeProvider = null)
    {
        if (subject is null || criterion is null || !subject.IsAuthenticated)
        {
            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!HasCompleteGroups(subject, now))
        {
            return false;
        }

        var claims = CollectClaims(subject);
        var resolvedRealm = realm ?? ResolveRealm(subject, claims, realmRegistry);

        if (HasExplicitGuestIndicator(claims))
        {
            return false;
        }

        if (IsInEmployeeGroup(subject.GroupSids, criterion.EmployeeGroupSids))
        {
            return true;
        }

        var realmKind = resolvedRealm?.Kind
            ?? subject.RealmKind
            ?? SecurityPrincipalContext.ReadPipelineRealmKind(subject.Principal)
            ?? ReadPipelineRealmKindFromClaims(claims);

        if (realmKind != IdentityRealmKind.EntraWorkforce)
        {
            return false;
        }

        if (!TryReadSingleGuidClaim(claims, ["tid", "http://schemas.microsoft.com/identity/claims/tenantid"], out var tokenTid))
        {
            return false;
        }

        if (!MatchesTrustedTenantId(tokenTid, criterion.TrustedIssuerTenantIds))
        {
            return false;
        }

        if (!HasTenantBoundEntraIssuer(claims, tokenTid, resolvedRealm))
        {
            return false;
        }

        if (!HasValidatedAudience(claims, resolvedRealm))
        {
            return false;
        }

        if (IsServicePrincipal(subject, claims))
        {
            return IsTrustedServicePrincipal(claims, criterion, resolvedRealm);
        }

        if (criterion.RequireMemberIndicator)
        {
            var acct = FindSingleClaimValue(claims, "acct");
            return string.Equals(acct, "0", StringComparison.Ordinal);
        }

        return true;
    }

    private static bool HasCompleteGroups(SecurityPrincipalContext subject, DateTimeOffset now)
    {
        if (subject.GroupResolutionStatus != GroupResolutionStatus.Complete)
        {
            return false;
        }

        if (GroupResolutionClaims.HasAnyDegradedClaim(subject.Claims))
        {
            return false;
        }

        if (subject.GroupsExpireAt is { } expiresAt && expiresAt <= now)
        {
            return false;
        }

        if (subject.Principal is not null &&
            (GroupResolutionClaims.IsMarkedDegraded(subject.Principal) || GroupResolutionClaims.IsExpired(subject.Principal, now)))
        {
            return false;
        }

        return true;
    }

    private static List<Claim> CollectClaims(SecurityPrincipalContext subject)
    {
        if (subject.Claims is null && subject.Principal is null)
        {
            return [];
        }

        if (subject.Claims is null)
        {
            return [.. subject.Principal!.Claims];
        }

        if (subject.Principal is null)
        {
            return [.. subject.Claims];
        }

        var seen = new HashSet<(string Type, string Value, string Issuer)>();
        var merged = new List<Claim>();
        foreach (var c in subject.Claims.Concat(subject.Principal.Claims))
        {
            if (seen.Add((c.Type, c.Value, c.Issuer)))
            {
                merged.Add(c);
            }
        }

        return merged;
    }

    private static IdentityRealm? ResolveRealm(
        SecurityPrincipalContext subject,
        IReadOnlyList<Claim> claims,
        IIdentityRealmRegistry? registry)
    {
        if (registry is null)
        {
            return null;
        }

        var realmId = FindFirstClaimValue(claims, "autheris:realm");
        if (!string.IsNullOrWhiteSpace(realmId) && registry.GetRealm(realmId) is { } byId)
        {
            return byId;
        }

        var iss = FindSingleClaimValue(claims, "iss");
        return string.IsNullOrWhiteSpace(iss) ? null : registry.ResolveRealmByIssuer(iss);
    }

    private static IdentityRealmKind? ReadPipelineRealmKindFromClaims(IReadOnlyList<Claim> claims)
    {
        foreach (var claim in claims)
        {
            if (string.Equals(claim.Type, "autheris:realm_kind", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(claim.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal) &&
                Enum.TryParse<IdentityRealmKind>(claim.Value, ignoreCase: true, out var kind))
            {
                return kind;
            }
        }

        return null;
    }

    private static bool HasExplicitGuestIndicator(IReadOnlyList<Claim> claims)
    {
        foreach (var claim in claims)
        {
            if (string.Equals(claim.Type, "acct", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(claim.Value?.Trim(), "1", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (IsUpnClaimType(claim.Type) &&
                     claim.Value?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        var idp = FindFirstClaimValue(claims, "idp")
            ?? FindFirstClaimValue(claims, "http://schemas.microsoft.com/identity/claims/identityprovider");
        if (!string.IsNullOrWhiteSpace(idp))
        {
            var iss = FindFirstClaimValue(claims, "iss");
            if (string.IsNullOrWhiteSpace(iss))
            {
                return true;
            }

            if (TryExtractEntraTenantGuid(iss, out var issuerTid))
            {
                if (Guid.TryParse(idp.Trim(), out var idpGuid))
                {
                    if (idpGuid != issuerTid)
                    {
                        return true;
                    }
                }
                else if (TryExtractEntraTenantGuid(idp, out var idpUrlTid))
                {
                    if (idpUrlTid != issuerTid)
                    {
                        return true;
                    }
                }
                else if (!string.Equals(TrimTrailingSlash(idp), TrimTrailingSlash(iss), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (!string.Equals(TrimTrailingSlash(idp), TrimTrailingSlash(iss), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInEmployeeGroup(IReadOnlySet<Sid> subjectGroups, IReadOnlyList<string> employeeGroupSids)
    {
        if (subjectGroups.Count == 0 || employeeGroupSids.Count == 0)
        {
            return false;
        }

        var configured = new HashSet<string>(employeeGroupSids.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        if (configured.Count == 0)
        {
            return false;
        }

        foreach (var groupSid in subjectGroups)
        {
            if (configured.Contains(groupSid.Value.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadSingleGuidClaim(IReadOnlyList<Claim> claims, IReadOnlyList<string> claimTypes, out Guid parsed)
    {
        parsed = default;
        string? raw = null;
        foreach (var claim in claims)
        {
            if (!claimTypes.Any(t => string.Equals(claim.Type, t, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var value = claim.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (raw is null)
            {
                raw = value;
            }
            else if (!string.Equals(raw, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return raw is not null && Guid.TryParse(raw, out parsed) && parsed != Guid.Empty;
    }

    private static bool MatchesTrustedTenantId(Guid tokenTid, IReadOnlyList<string> trustedTenantIds)
    {
        if (trustedTenantIds.Count == 0)
        {
            return false;
        }

        foreach (var entry in trustedTenantIds)
        {
            if (Guid.TryParse(entry?.Trim(), out var trustedGuid) && trustedGuid != Guid.Empty && trustedGuid == tokenTid)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTenantBoundEntraIssuer(IReadOnlyList<Claim> claims, Guid tokenTid, IdentityRealm? realm)
    {
        var iss = FindSingleClaimValue(claims, "iss");
        if (string.IsNullOrWhiteSpace(iss) || !TryExtractEntraTenantGuid(iss, out var issuerTid) || issuerTid != tokenTid)
        {
            return false;
        }

        if (realm is { Issuers.Count: > 0 })
        {
            var normalizedIss = TrimTrailingSlash(iss);
            return realm.Issuers.Any(r =>
                !string.IsNullOrWhiteSpace(r) &&
                string.Equals(TrimTrailingSlash(r), normalizedIss, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private static bool TryExtractEntraTenantGuid(string rawUrl, out Guid tenantGuid)
    {
        tenantGuid = default;
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!EntraHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstSegment = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return !string.IsNullOrWhiteSpace(firstSegment) &&
               Guid.TryParse(firstSegment, out tenantGuid) &&
               tenantGuid != Guid.Empty;
    }

    private static bool HasValidatedAudience(IReadOnlyList<Claim> claims, IdentityRealm? realm)
    {
        var audiences = claims
            .Where(c => string.Equals(c.Type, "aud", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value.Trim())
            .ToList();

        if (audiences.Count == 0)
        {
            return false;
        }

        if (realm is { Audiences.Count: > 0 })
        {
            return audiences.Any(a => realm.Audiences.Contains(a, StringComparer.OrdinalIgnoreCase));
        }

        return true;
    }

    private static bool IsServicePrincipal(SecurityPrincipalContext subject, IReadOnlyList<Claim> claims)
    {
        if (claims.Any(c =>
            string.Equals(c.Type, McpProtocolHandler.IdentityKindClaimType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Value, McpProtocolHandler.ApplicationIdentityKind, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (claims.Any(c =>
            string.Equals(c.Type, "autheris:subject_kind", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal) &&
            string.Equals(c.Value, "sp", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.Equals(FindFirstClaimValue(claims, "idtyp"), "app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var gty = FindFirstClaimValue(claims, "gty");
        if (string.Equals(gty, "client-credentials", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(gty, "client_credentials", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (subject.Principal is not null && AppOnlyPrincipal.Is(subject.Principal))
        {
            return true;
        }

        var hasClientId = claims.Any(c =>
            (string.Equals(c.Type, "azp", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(c.Type, "appid", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(c.Value));
        if (hasClientId &&
            TokenAccessScope.GetScopeSet(claims).Count == 0 &&
            !claims.Any(c => IsUpnClaimType(c.Type) ||
                             string.Equals(c.Type, "acct", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(c.Type, "email", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(c.Type, ClaimTypes.Email, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsTrustedServicePrincipal(
        IReadOnlyList<Claim> claims,
        InternalAudiencePolicy criterion,
        IdentityRealm? realm)
    {
        if (criterion.TrustedServicePrincipalClientIds.Count == 0)
        {
            return false;
        }

        var azp = FindSingleClaimValue(claims, "azp");
        var appid = FindSingleClaimValue(claims, "appid");
        var clientIdClaim = FindSingleClaimValue(claims, "client_id");
        string? clientId = null;
        foreach (var candidate in new[] { azp, appid, clientIdClaim })
        {
            if (candidate is null)
            {
                continue;
            }

            if (clientId is null)
            {
                clientId = candidate;
            }
            else if (!string.Equals(clientId, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        if (!criterion.TrustedServicePrincipalClientIds.Contains(clientId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (realm?.AllowedClientIds is { Count: > 0 } allowed &&
            !allowed.Contains(clientId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsUpnClaimType(string type) =>
        string.Equals(type, "upn", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, ClaimTypes.Upn, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "preferred_username", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "unique_name", StringComparison.OrdinalIgnoreCase);

    private static string? FindFirstClaimValue(IReadOnlyList<Claim> claims, string type)
    {
        foreach (var c in claims)
        {
            if (string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Value))
            {
                return c.Value.Trim();
            }
        }

        return null;
    }

    private static string? FindSingleClaimValue(IReadOnlyList<Claim> claims, string type)
    {
        string? value = null;
        foreach (var c in claims)
        {
            if (!string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var current = c.Value?.Trim();
            if (string.IsNullOrWhiteSpace(current))
            {
                return null;
            }

            if (value is null)
            {
                value = current;
            }
            else if (!string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return value;
    }

    private static string TrimTrailingSlash(string value) => value.Trim().TrimEnd('/');
}
