namespace Autheris.Api.Middleware;

using System;
using System.Security;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Architecture Phase 1: Ingress middleware establishing an immutable SecurityPrincipalContext.
/// Subsumes tenant and identity resolution, guaranteeing fail-closed cross-tenant isolation.
/// </summary>
public sealed class SecurityContextResolutionMiddleware(RequestDelegate next)
{
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

        // Populate both canonical context and legacy item key for zero-downtime migration
        context.Items[SecurityPrincipalContext.ItemKey] = securityContext;
        context.Items[TenantResolutionMiddleware.TenantIdItemKey] = securityContext.TenantId;

        await next(context).ConfigureAwait(false);
    }
}
