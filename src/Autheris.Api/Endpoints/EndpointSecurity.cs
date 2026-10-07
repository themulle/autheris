namespace Autheris.Api.Endpoints;

using System;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Shared, unit-testable security helpers for REST endpoint handlers (SEC C-05, M-07, M-11, M-12).
/// </summary>
internal static class EndpointSecurity
{
    /// <summary>
    /// Phase 1: Resolves the canonical SecurityPrincipalContext established by SecurityContextResolutionMiddleware.
    /// </summary>
    public static SecurityPrincipalContext GetSecurityContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) && item is SecurityPrincipalContext ctx)
        {
            return ctx;
        }

        return SecurityContextFactory.CreateFromHttpContext(context);
    }

    /// <summary>
    /// Resolves the tenant of the current request exactly like the rest of the pipeline:
    /// <c>SecurityPrincipalContext</c> first, then legacy item keys and claims.
    /// </summary>
    public static TenantId GetRequestTenant(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) && item is SecurityPrincipalContext ctx)
        {
            return ctx.TenantId;
        }

        if (context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var itemTenant) && itemTenant is TenantId resolved)
        {
            return resolved;
        }

        // A malformed tenant claim throws SecurityException (fail closed) instead of becoming the legacy tenant.
        return context.User.GetTenantId();
    }

    /// <summary>Cluster-wide administrator (may act across tenants).</summary>
    public static bool IsClusterAdmin(ClaimsPrincipal? principal)
        => GatewayPolicies.HasRole(principal, GatewayRole.ClusterAdmin);

    /// <summary>SEC M-1 / M-4: Canonical ClusterAdmin role check (strictly global, not mapped from tenant-scoped aliases).</summary>
    public static bool IsCanonicalClusterAdmin(ClaimsPrincipal? principal)
        => Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

    /// <summary>
    /// SEC M-11: Global (tenant-independent) governance administrators: GovernanceAdmin or ClusterAdmin.
    /// </summary>
    public static bool IsGlobalGovernanceAdmin(ClaimsPrincipal? principal)
        => GatewayPolicies.HasRole(principal, GatewayRole.GovernanceAdmin);

    /// <summary>SEC C-05: Approver roles (DataSteward, DataOwner, GovernanceAdmin) or ClusterAdmin.</summary>
    public static bool IsApprover(ClaimsPrincipal? principal)
        => GatewayPolicies.HasAnyRole(principal, [GatewayRole.DataSteward, GatewayRole.DataOwner, GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin]);

    /// <summary>
    /// Canonical caller identity from HttpContext using SecurityPrincipalContext if authenticated,
    /// falling back to the principal's claims.
    /// </summary>
    public static string? GetCallerIdentity(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SecurityPrincipalContext secContext;
        try
        {
            secContext = GetSecurityContext(context);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // RV-02: authenticated without SID -> treated as unauthenticated (401)
        }

        if (secContext is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(secContext.UserSid.Value))
        {
            return secContext.UserSid.Value;
        }

        return GetCallerIdentity(context.User);
    }

    /// <summary>
    /// Canonical caller identity, identical to the requester identity used elsewhere (<c>GetUserSid()</c>).
    /// </summary>
    public static string? GetCallerIdentity(ClaimsPrincipal? principal)
    {
        var sid = principal.GetUserSid()?.Value;
        if (!string.IsNullOrWhiteSpace(sid))
        {
            return sid;
        }

        var name = principal?.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// SEC M-34: Parses a webhook timestamp header. Accepts Unix time in seconds (as signed by the catalog
    /// webhook contract "{unixSeconds}.{payload}"), Unix time in milliseconds and ISO 8601 / RFC 1123 values
    /// (interpreted as UTC when no offset is given).
    /// </summary>
    public static bool TryParseWebhookTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            {
                return false;
            }

            if (unix <= MaxUnixSeconds)
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            }

            if (unix <= MaxUnixMilliseconds)
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unix);
                return true;
            }

            return false;
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    }

    // Seconds up to year 5138 (11 digits); larger values are interpreted as milliseconds.
    private const long MaxUnixSeconds = 99_999_999_999L;
    private const long MaxUnixMilliseconds = 253_402_300_799_999L;

    /// <summary>
    /// SEC M-07: Reads the request body as UTF-8 with a hard byte limit (independent of Content-Length / chunked encoding).
    /// Returns a 413 result instead of the body when the limit is exceeded.
    /// </summary>
    public static async Task<(string? Body, IResult? Error)> TryReadBodyAsync(
        HttpRequest request,
        long maxBytes,
        string tooLargeMessage,
        CancellationToken ct)
    {
        try
        {
            var body = await request.ReadBodyAsStringAsync(maxBytes, ct).ConfigureAwait(false);
            if (body.Length > 0 && body[0] == '﻿')
            {
                body = body[1..];
            }

            return (body, null);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Results.Json(new { error = tooLargeMessage }, statusCode: StatusCodes.Status413PayloadTooLarge));
        }
    }

    /// <summary>
    /// SEC M-07: For handlers that pass the raw request stream on, tightens the server-side body limit.
    /// Returns a 413 result when the declared Content-Length already exceeds the limit.
    /// </summary>
    public static IResult? TryApplyBodyLimit(HttpRequest request, long maxBytes, string tooLargeMessage)
    {
        try
        {
            request.ApplyMaxRequestBodySize(maxBytes);
            return null;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(new { error = tooLargeMessage }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    }

    /// <summary>
    /// SEC M-07: Wraps a stream-consuming operation so that hitting the tightened server-side body limit
    /// (Kestrel raises <see cref="BadHttpRequestException"/> with 413) yields a clean 413 result.
    /// </summary>
    public static async Task<IResult> WithBodyLimitAsync(Func<Task<IResult>> action, string tooLargeMessage)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(new { error = tooLargeMessage }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
    }
}
