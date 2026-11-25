namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Shared submission controls for the consent request entry points so subject restrictions, rate limiting and
/// validity resolution cannot drift across paths.
/// </summary>
public static class ConsentSubmissionGuards
{
    public static void EnsurePersonSubject(ClaimsPrincipal? principal, IIdentitySubjectResolver? subjectResolver = null)
    {
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new ConsentRequestException("FORBIDDEN", "Authenticated principal is required.");
        }

        if (string.Equals(principal.FindFirst("idtyp")?.Value, "app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentRequestException("FORBIDDEN", "Machine tokens (idtyp=app) cannot execute self-service access requests.");
        }

        if (principal.FindFirst(ClaimTypes.AuthenticationMethod)?.Value is "x509" or "Certificate"
            || principal.FindFirst("auth_method")?.Value is "mTLS" or "cert" or "certificate")
        {
            throw new ConsentRequestException("FORBIDDEN", "Certificate subjects cannot execute self-service access requests.");
        }

        if (principal.HasClaim(c => c.Type == "roles" && c.Value == "AppRole")
            && !principal.HasClaim(c => c.Type == "scp" || c.Type == "http://schemas.microsoft.com/identity/claims/scope"))
        {
            throw new ConsentRequestException("FORBIDDEN", "Application tokens cannot execute self-service access requests.");
        }

        if (subjectResolver != null)
        {
            var subject = subjectResolver.ResolveSubject(principal);
            if (subject.Provider == IdentityProviderType.ClientCertificate || subject.Type != SubjectType.User)
            {
                throw new ConsentRequestException("FORBIDDEN", "Self-service access requests require a person subject.");
            }
        }
    }

    public static void EnsureSelfServiceEnabled(GatewayOptions? gatewayOptions)
    {
        if (gatewayOptions?.Authentication.EntraId.SelfServiceEnabled == false)
        {
            throw new ConsentRequestException("FORBIDDEN", "Self-service access requests are disabled.");
        }
    }

    public static async Task CheckAccessRequestRateAsync(
        TenantId tenantId,
        Sid userSid,
        IRateLimiterService? rateLimiter,
        GatewayOptions? gatewayOptions,
        CatalogDiscoveryOptions? discoveryOptions,
        CancellationToken ct)
    {
        if (rateLimiter is null || gatewayOptions is null)
        {
            return;
        }

        var retryAfter = await DiscoveryCallerGate.CheckAccessRequestRateAsync(
            tenantId,
            userSid,
            discoveryOptions?.Discovery ?? new CatalogDiscoverySettings(),
            gatewayOptions,
            rateLimiter,
            ct).ConfigureAwait(false);
        if (retryAfter is { } wait)
        {
            throw new ConsentRequestException("RATE_LIMIT_EXCEEDED", "Access request rate limit exceeded.", wait);
        }
    }

    public static DateTimeOffset ResolveRequestedValidTo(
        IGrantValidityPolicy validityPolicy,
        DateTimeOffset now,
        DateTimeOffset? requestedValidFrom,
        DateTimeOffset? requestedValidTo,
        int? durationDays = null,
        string? requestedDuration = null)
    {
        ArgumentNullException.ThrowIfNull(validityPolicy);
        try
        {
            return validityPolicy.ResolveRequestedValidTo(
                now,
                requestedValidFrom,
                requestedValidTo,
                string.IsNullOrWhiteSpace(requestedDuration) && requestedValidTo == null ? durationDays : null,
                requestedDuration);
        }
        catch (GrantValidityException ex)
        {
            throw new ConsentRequestException(ex.ErrorCode, ex.Message);
        }
    }
}
