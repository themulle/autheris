namespace Autheris.Application.Services;

using System;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class IdentitySubjectResolver : IIdentitySubjectResolver
{
    public SubjectIdentity ResolveSubject(ClaimsPrincipal? principal)
    {
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            return new SubjectIdentity(
                Id: "anonymous",
                Type: SubjectType.User,
                Provider: IdentityProviderType.BasicAuth,
                DisplayName: "Anonymous"
            );
        }

        var roles = principal.GetUserRoles();

        // 1. Resolve Tenant if present
        TenantId? tenant = null;
        var tidClaim = principal.FindFirst("tenant_id")?.Value
            ?? principal.FindFirst("tid")?.Value
            ?? principal.FindFirst("tenant")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        if (TenantId.TryParse(tidClaim, out var parsedTenant))
        {
            tenant = parsedTenant;
        }

        // 2. Check for mTLS / Client Certificate
        var authType = principal.Identity.AuthenticationType;
        var certThumbprint = principal.FindFirst("x509_thumbprint")?.Value
            ?? principal.FindFirst("client_cert_thumbprint")?.Value;

        if (!string.IsNullOrWhiteSpace(certThumbprint) ||
            string.Equals(authType, "Certificate", StringComparison.OrdinalIgnoreCase))
        {
            var certId = certThumbprint ?? principal.Identity.Name ?? "cert:unknown";
            return new SubjectIdentity(
                Id: certId,
                Type: SubjectType.ServicePrincipal,
                Provider: IdentityProviderType.ClientCertificate,
                DisplayName: $"mTLS:{certId}",
                Tenant: tenant,
                Roles: roles
            );
        }

        // 3. Check for M2M / Service Principal (Entra ID App-Only or Client Credentials)
        var idType = principal.FindFirst("idtyp")?.Value;
        var appId = principal.FindFirst("appid")?.Value
            ?? principal.FindFirst("client_id")?.Value
            ?? principal.FindFirst("azp")?.Value;

        var isAppOnly = string.Equals(idType, "app", StringComparison.OrdinalIgnoreCase);

        var hasHumanIdentityClaim = principal.FindFirst(ClaimTypes.Upn) != null
            || principal.FindFirst("upn") != null
            || principal.FindFirst(ClaimTypes.Email) != null
            || principal.FindFirst("preferred_username") != null
            || principal.FindFirst("unique_name") != null;

        if (isAppOnly || (!string.IsNullOrWhiteSpace(appId) && !hasHumanIdentityClaim))
        {
            var spId = principal.FindFirst("oid")?.Value
                ?? principal.FindFirst("sub")?.Value
                ?? appId
                ?? "spn:unknown";

            var appDisplayName = principal.FindFirst("app_displayname")?.Value
                ?? principal.FindFirst("appName")?.Value
                ?? appId
                ?? spId;

            var provider = tenant.HasValue ? IdentityProviderType.EntraId : IdentityProviderType.ServiceAccount;

            return new SubjectIdentity(
                Id: spId,
                Type: SubjectType.ServicePrincipal,
                Provider: provider,
                DisplayName: appDisplayName,
                Tenant: tenant,
                Roles: roles
            );
        }

        // 4. Check for Entra ID Interactive User (OIDC / OAuth2)
        var oid = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        if (!string.IsNullOrWhiteSpace(oid))
        {
            var userDisplayName = principal.FindFirst("preferred_username")?.Value
                ?? principal.FindFirst(ClaimTypes.Upn)?.Value
                ?? principal.FindFirst("upn")?.Value
                ?? principal.FindFirst(ClaimTypes.Email)?.Value
                ?? principal.Identity.Name
                ?? oid;

            return new SubjectIdentity(
                Id: oid,
                Type: SubjectType.User,
                Provider: IdentityProviderType.EntraId,
                DisplayName: userDisplayName,
                Tenant: tenant,
                Roles: roles
            );
        }

        // 5. Check for On-Premise Active Directory User (Kerberos / Negotiate)
        var primarySid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value
            ?? principal.FindFirst("primarysid")?.Value
            ?? principal.FindFirst("objectSid")?.Value
            ?? principal.FindFirst("onprem_sid")?.Value;

        if (!string.IsNullOrWhiteSpace(primarySid) && primarySid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return new SubjectIdentity(
                Id: primarySid,
                Type: SubjectType.User,
                Provider: IdentityProviderType.ActiveDirectory,
                DisplayName: principal.Identity.Name ?? primarySid,
                Tenant: tenant,
                Roles: roles
            );
        }

        // 6. Generic / Fallback User (BasicAuth, ForwardAuth, TestAuth)
        var genericSid = principal.GetUserSid()?.Value ?? principal.Identity.Name ?? "user:unknown";
        var resolvedProvider = authType switch
        {
            "TestAuth" => IdentityProviderType.TestAuth,
            "ForwardAuth" => IdentityProviderType.ForwardAuth,
            _ => IdentityProviderType.BasicAuth
        };

        return new SubjectIdentity(
            Id: genericSid,
            Type: SubjectType.User,
            Provider: resolvedProvider,
            DisplayName: principal.Identity.Name ?? genericSid,
            Tenant: tenant,
            Roles: roles
        );
    }
}
