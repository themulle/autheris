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
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// Shared submission controls for all consent request entry points (single-table, bundled multi-table,
/// and access-profile assignments) so validation, subject restrictions, rate limiting, workflow routing,
/// route duration caps, and requester-identifier capture cannot drift across paths.
/// </summary>
public static class ConsentSubmissionPipeline
{
    public const int DefaultMinJustificationLength = 5;
    public const int DefaultMaxJustificationLength = 2000;

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

    public static string ValidateJustification(
        string? justification,
        int minLength = DefaultMinJustificationLength,
        int maxLength = DefaultMaxJustificationLength)
    {
        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < minLength)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", $"A justification of at least {minLength} characters is required.");
        }

        var trimmed = justification.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ConsentRequestException("VALIDATION_ERROR", $"The justification must not exceed {maxLength} characters.");
        }

        return trimmed;
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

    public static async Task<(ConsentWorkflowMode Mode, Guid? ExternalSystemId, DateTimeOffset EffectiveValidTo)> ResolveWorkflowRouteAsync(
        TenantId tenantId,
        IReadOnlyList<TableIdentifier> tables,
        DateTimeOffset effectiveStart,
        DateTimeOffset resolvedValidTo,
        bool hasExplicitDuration,
        IConsentWorkflowRouter? workflowRouter,
        IConsentWorkflowConfigRepository? workflowConfigRepository,
        CancellationToken ct)
    {
        if (workflowRouter != null)
        {
            var resolution = tables.Count == 1
                ? await workflowRouter.ResolveForTableAsync(tenantId, tables[0], ct).ConfigureAwait(false)
                : await workflowRouter.ResolveForTablesAsync(tenantId, tables, ct).ConfigureAwait(false);

            var effectiveValidTo = ApplyRouteDurationCap(effectiveStart, resolvedValidTo, resolution.MaxDurationDays, hasExplicitDuration);
            var extId = resolution.Mode == ConsentWorkflowMode.External ? resolution.ExternalSystemId : null;
            return (resolution.Mode, extId, effectiveValidTo);
        }

        if (workflowConfigRepository == null || tables.Count == 0)
        {
            return (ConsentWorkflowMode.Internal, null, resolvedValidTo);
        }

        ConsentWorkflowMode mode = ConsentWorkflowMode.Internal;
        Guid? externalSystemId = null;
        var currentValidTo = resolvedValidTo;
        foreach (var tableId in tables)
        {
            var route = await workflowConfigRepository.ResolveWorkflowRouteAsync(tenantId, tableId, ct).ConfigureAwait(false);
            if (route is null)
            {
                continue;
            }

            if (route.MaxDurationDays > 0)
            {
                currentValidTo = ApplyRouteDurationCap(effectiveStart, currentValidTo, route.MaxDurationDays, hasExplicitDuration);
            }

            if (route.Mode == ConsentWorkflowMode.External)
            {
                if (mode == ConsentWorkflowMode.External && externalSystemId != route.ExternalSystemId)
                {
                    throw new ConsentRequestException(
                        "VALIDATION_ERROR",
                        "Bundled targets resolve to incompatible external approval systems.");
                }

                mode = ConsentWorkflowMode.External;
                externalSystemId = route.ExternalSystemId;
            }
        }

        return (mode, externalSystemId, currentValidTo);
    }

    public static DateTimeOffset ApplyRouteDurationCap(
        DateTimeOffset effectiveStart,
        DateTimeOffset resolvedValidTo,
        int maxDurationDays,
        bool hasExplicitDuration)
    {
        if (maxDurationDays <= 0 || maxDurationDays == int.MaxValue)
        {
            return resolvedValidTo;
        }

        var routeMaxEnd = GrantTimeMath.AddSaturating(effectiveStart, GrantTimeMath.DaysToSpan(Math.Max(1, maxDurationDays)));
        if (resolvedValidTo > routeMaxEnd)
        {
            if (hasExplicitDuration)
            {
                throw new ConsentRequestException(
                    "VALIDITY_EXCEEDS_MAXIMUM",
                    $"Requested grant duration exceeds the configured route maximum of {maxDurationDays} days.");
            }

            return routeMaxEnd;
        }

        return resolvedValidTo;
    }

    public static List<string> ExtractRequesterIdentifiers(ClaimsPrincipal? principal, string? fallbackSid = null) =>
        [.. SeparationOfDutiesService.CollectSubjectIdentifiers(principal, fallbackSid)];
}
