namespace Autheris.Api.Middleware;

using System;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantIdItemKey = "TenantId";
    public const string TenantHeaderName = "X-Tenant-ID";
    public const string TenantHeaderNameAlt = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        string? claimTenant = null;
        if (context.User.Identity?.IsAuthenticated == true)
        {
            claimTenant = context.User.FindFirst("tenant_id")?.Value
                ?? context.User.FindFirst("tid")?.Value
                ?? context.User.FindFirst("tenant")?.Value
                ?? context.User.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        }

        string? headerTenant = null;
        if (context.Request.Headers.TryGetValue(TenantHeaderName, out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        {
            headerTenant = headerVal.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue(TenantHeaderNameAlt, out var headerValAlt) && !string.IsNullOrWhiteSpace(headerValAlt))
        {
            headerTenant = headerValAlt.ToString().Trim();
        }

        string? rawTenant;

        // Security Guard: An authenticated user cannot spoof or switch to another tenant via header.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!string.IsNullOrWhiteSpace(claimTenant))
            {
                if (!string.IsNullOrWhiteSpace(headerTenant) &&
                    !string.Equals(claimTenant, headerTenant, StringComparison.OrdinalIgnoreCase))
                {
                    bool isAdmin = context.User.IsInRole("GatewayAdmin") || context.User.IsInRole("PlatformAdmin");
                    if (!isAdmin)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"errors\":[{\"message\":\"Cross-tenant access forbidden. Requested header tenant does not match authenticated token claim.\",\"code\":\"CROSS_TENANT_ACCESS_FORBIDDEN\"}]}");
                        return;
                    }
                    rawTenant = headerTenant;
                }
                else
                {
                    rawTenant = claimTenant;
                }
            }
            else
            {
                // Authenticated user WITHOUT a verified tenant claim:
                // Cannot select arbitrary tenants via client header unless possessing admin privileges.
                if (!string.IsNullOrWhiteSpace(headerTenant))
                {
                    bool isAdmin = context.User.IsInRole("GatewayAdmin") || context.User.IsInRole("PlatformAdmin");
                    if (!isAdmin)
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"errors\":[{\"message\":\"Cross-tenant access forbidden. Authenticated user does not possess a tenant claim and is not authorized to select arbitrary tenants via header.\",\"code\":\"CROSS_TENANT_ACCESS_FORBIDDEN\"}]}");
                        return;
                    }
                    rawTenant = headerTenant;
                }
                else
                {
                    rawTenant = null;
                }
            }
        }
        else
        {
            rawTenant = headerTenant;
        }

        TenantId tenantId;
        if (!string.IsNullOrWhiteSpace(rawTenant))
        {
            try
            {
                tenantId = new TenantId(rawTenant);
            }
            catch (ArgumentException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"errors\":[{\"message\":\"Invalid tenant identifier.\",\"code\":\"INVALID_TENANT_ID\"}]}");
                return;
            }
        }
        else
        {
            tenantId = TenantId.LegacySingleTenant;
        }

        context.Items[TenantIdItemKey] = tenantId;
        await next(context);
    }
}
