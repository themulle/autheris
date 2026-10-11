namespace Autheris.Api.Middleware;

using System;
using System.Security;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Architecture Phase 1: Ingress middleware establishing an immutable SecurityPrincipalContext.
/// Subsumes tenant and identity resolution, guaranteeing fail-closed cross-tenant isolation.
/// </summary>
public sealed class SecurityContextResolutionMiddleware(RequestDelegate next)
{
    public const string TenantCollisionCode = "TENANT_ID_COLLISION";

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

        try
        {
            var audit = context.RequestServices?.GetService<IAuditLogRepository>();
            if (audit != null)
            {
                await audit.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = securityContext.TenantId,
                    EventType = AuditEventTypes.TenantCollisionDenied,
                    ActorSid = securityContext.UserSid,
                    TargetTable = string.Empty,
                    Decision = "DENY",
                    TraceId = context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason = unresolved ? "tenant collision guard could not be resolved (fail closed)" : "tenant id collides case-insensitively with another tenant id", path = context.Request.Path.Value })
                }, context.RequestAborted).ConfigureAwait(false);
            }
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
        return true;
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

        // Populate both canonical context and legacy item key for zero-downtime migration
        context.Items[SecurityPrincipalContext.ItemKey] = securityContext;
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = securityContext.TenantId;

        await next(context).ConfigureAwait(false);
    }
}
