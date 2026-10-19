namespace Autheris.Api.Middleware;

using System;
using System.Security;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Architecture Phase 1: Ingress middleware establishing an immutable SecurityPrincipalContext.
/// Subsumes tenant and identity resolution, guaranteeing fail-closed cross-tenant isolation.
/// </summary>
public sealed class SecurityContextResolutionMiddleware(RequestDelegate next)
{
    public const string TenantCollisionCode = "TENANT_ID_COLLISION";
    public const string TenantUnknownCode = "TENANT_UNKNOWN";
    public const string TenantRegistryUnavailableCode = "TENANT_REGISTRY_UNAVAILABLE";
    public const string TenantRegistrationRateLimitedCode = "TENANT_REGISTRATION_RATE_LIMITED";

    // Cached per options instance: a configuration reload yields a new instance, which rebuilds the guard.
    // CR-ADG-40: one immutable reference, so a reader can never pair new options with an old guard (a value tuple is copied non-atomically).
    private sealed record GuardCacheEntry(GatewayOptions Options, TenantCollisionGuard Guard);

    private volatile GuardCacheEntry? _cached;

    private TenantCollisionGuard? ResolveGuard(HttpContext context)
    {
        GatewayOptions? options;
        try
        {
            options = context.RequestServices?.GetService<IOptionsMonitor<GatewayOptions>>()?.CurrentValue;
        }
        catch (Exception)
        {
            return null;
        }

        if (options == null) return null;
        var cached = _cached;
        if (cached is not null && ReferenceEquals(cached.Options, options)) return cached.Guard;
        try
        {
            var guard = TenantCollisionGuard.FromOptions(options);
            _cached = new GuardCacheEntry(options, guard);
            return guard;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>B-1: a tenant whose id collides case-insensitively with another configured tenant is denied (fail closed); so is any request whose guard cannot be resolved.</summary>
    private async Task<bool> DenyCollidingTenantAsync(HttpContext context, SecurityPrincipalContext securityContext)
    {
        var guard = ResolveGuard(context);
        var unresolved = guard == null;
        if (!unresolved && (!guard!.HasCollisions || !guard.IsDenied(securityContext.TenantId.Value)))
        {
            return false;
        }

        await WriteCollisionDenialAsync(context, securityContext,
            unresolved ? "tenant collision guard could not be resolved (fail closed)" : "tenant id collides case-insensitively with another tenant id").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// CR-ADG-31 / SEC-X1-13: registers the tenant of an authenticated principal in the tenant registry (never for anonymous
    /// requests; <see cref="SecurityContextFactory"/> has validated the id as a <c>TenantId</c>). Returns true when the request was answered:
    /// 403 for a collision, 429 when the issuer exhausted its budget for new tenants, 503 when the registry cannot decide.
    /// Without a registered <see cref="ITenantRegistry"/> the legacy behaviour is unchanged.
    /// </summary>
    private async Task<bool> DenyByRegistryAsync(HttpContext context, SecurityPrincipalContext securityContext)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var registry = context.RequestServices?.GetService<ITenantRegistry>();
        if (registry == null)
        {
            return false;
        }

        TenantRegistration? result;
        try
        {
            // M-A2: a tenant that a ClusterAdmin selected by header is looked up only. A header can never register a tenant or mark a
            // collision (which would lock the tenant's whole spelling group); an unknown tenant is refused.
            result = securityContext.TenantSelectedByHeader
                ? await registry.TryGetAsync(securityContext.TenantId, context.RequestAborted).ConfigureAwait(false)
                : await registry.RegisterOrGetAsync(securityContext.TenantId, BudgetKey(context.User), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<SecurityContextResolutionMiddleware>()
                .LogError(ex, "The tenant registry is unavailable; the request is denied (fail closed).");
            await WriteJsonAsync(context, StatusCodes.Status503ServiceUnavailable, "The tenant registry is unavailable.", TenantRegistryUnavailableCode).ConfigureAwait(false);
            return true;
        }

        switch (result)
        {
            case TenantRegistration.Registered:
            case TenantRegistration.Existing:
                return false;
            case null:
                await WriteJsonAsync(context, StatusCodes.Status403Forbidden, "The selected tenant is not registered.", TenantUnknownCode).ConfigureAwait(false);
                return true;
            case TenantRegistration.RateLimited:
                await WriteJsonAsync(context, StatusCodes.Status429TooManyRequests, "Too many new tenants were registered for this identity provider issuer.", TenantRegistrationRateLimitedCode).ConfigureAwait(false);
                return true;
            default:
                await WriteCollisionDenialAsync(context, securityContext, "tenant id collides with a registered tenant id (tenant registry)").ConfigureAwait(false);
                return true;
        }
    }

    /// <summary>
    /// Phase 5 L6: the budget key of a new tenant registration: the issuer of the token, or one bucket per authentication scheme for a token
    /// without an issuer (API keys, forward auth), so that issuer-less callers do not share a single bucket.
    /// </summary>
    private static string BudgetKey(ClaimsPrincipal user)
    {
        var issuer = user.FindFirst("iss")?.Value;
        return !string.IsNullOrWhiteSpace(issuer) ? issuer : $"scheme:{user.Identity?.AuthenticationType ?? "unknown"}";
    }

    private static Task WriteJsonAsync(HttpContext context, int status, string message, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { errors = new[] { new { message, code } } }));
    }

    private static async Task WriteCollisionDenialAsync(HttpContext context, SecurityPrincipalContext securityContext, string reason)
    {
        try
        {
            // A denial never depends on the audit write: the best-effort helper logs and counts a failing sink instead of throwing.
            var audit = context.RequestServices.GetRequiredService<Autheris.Application.Audit.IAuditWriter>();
            await audit.RecordBestEffortAsync(new AuditLogEntry
            {
                TenantId = securityContext.TenantId,
                EventType = AuditEventTypes.TenantCollisionDenied,
                ActorSid = securityContext.UserSid,
                TargetTable = string.Empty,
                Decision = "DENY",
                TraceId = context.TraceIdentifier,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason, path = context.Request.Path.Value })
            }, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The denial does not depend on the audit write; a failing audit sink must not turn the denial into an allow or a 500.
            context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<SecurityContextResolutionMiddleware>()
                .LogError(ex, "Audit write for a tenant collision denial failed.");
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            "{\"errors\":[{\"message\":\"The tenant id is ambiguous (differs from another tenant id only in case) and is denied until an operator resolves the collision.\",\"code\":\"" + TenantCollisionCode + "\"}]}").ConfigureAwait(false);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        SecurityPrincipalContext securityContext;
        try
        {
            securityContext = SecurityContextFactory.CreateFromHttpContext(context);
        }
        catch (SecurityException ex)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                errors = new[] { new { message = ex.Message, code = "CROSS_TENANT_ACCESS_FORBIDDEN" } }
            })).ConfigureAwait(false);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // RV-02: authenticated principal without canonical SID – never fall back to a display name.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"errors\":[{\"message\":\"Authenticated user lacks a valid SID claim.\",\"code\":\"UNAUTHORIZED_NO_SID\"}]}").ConfigureAwait(false);
            return;
        }
        catch (ArgumentException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"errors\":[{\"message\":\"Invalid tenant identifier.\",\"code\":\"INVALID_TENANT_ID\"}]}").ConfigureAwait(false);
            return;
        }

        if (await DenyCollidingTenantAsync(context, securityContext).ConfigureAwait(false))
        {
            return;
        }

        if (await DenyByRegistryAsync(context, securityContext).ConfigureAwait(false))
        {
            return;
        }

        // Populate both canonical context and legacy item key for zero-downtime migration
        context.Items[SecurityPrincipalContext.ItemKey] = securityContext;
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = securityContext.TenantId;

        using (TelemetryTenantContext.Enter(securityContext.TenantId))
        {
            if (System.Diagnostics.Activity.Current != null && System.Diagnostics.Activity.Current.IsAllDataRequested)
            {
                System.Diagnostics.Activity.Current.SetTag(TelemetryAttributes.AutherisTenantId, securityContext.TenantId.Value);
            }

            await next(context).ConfigureAwait(false);
        }
    }
}
