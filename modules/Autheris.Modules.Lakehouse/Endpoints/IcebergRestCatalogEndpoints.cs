namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-DATA-05: Apache Iceberg REST Catalog (IRC) RFC endpoints with Dynamic STS Credential Vending.
/// </summary>
public static class IcebergRestCatalogEndpoints
{
    public static void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/v1");
        MapGroupRoutes(group);
    }

    public static IEndpointRouteBuilder MapIcebergRestCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1")
            .RequireAuthorization();
        MapGroupRoutes(group);
        return app;
    }

    private static void MapGroupRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/{prefix}/namespaces", async (
            string prefix,
            HttpContext context,
            [FromServices] IIcebergRestCatalogFederationService catalogService,
            CancellationToken ct) =>
        {
            var tenant = RequireTenant(context);
            var namespaces = await catalogService.ListNamespacesAsync(tenant, context.User, ct);
            var response = new IcebergListNamespacesResponse(namespaces.Select(ns => (IReadOnlyList<string>)new[] { ns }).ToList());
            return Results.Ok(response);
        });

        group.MapGet("/{prefix}/namespaces/{namespace}/tables", async (
            string prefix,
            string @namespace,
            HttpContext context,
            [FromServices] IIcebergRestCatalogFederationService catalogService,
            CancellationToken ct) =>
        {
            var tenant = RequireTenant(context);
            var tables = await catalogService.ListTablesAsync(tenant, @namespace, context.User, ct);
            var response = new IcebergListTablesResponse(
                tables.Select(t => new IcebergRestTableIdentifier(new[] { @namespace }, t)).ToList());
            return Results.Ok(response);
        });

        group.MapGet("/{prefix}/namespaces/{namespace}/tables/{table}", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            [FromServices] IIcebergRestCatalogFederationService catalogService,
            CancellationToken ct) =>
        {
            var tenant = RequireTenant(context);
            try
            {
                var response = await catalogService.LoadTableAsync(tenant, @namespace, table, context.User, ct);
                return Results.Ok(response);
            }
            catch (TransportNotAllowedException ex)
            {
                return HandleIcebergError(context, ex);
            }
            catch (SecurityException ex)
            {
                return HandleIcebergError(context, ex);
            }
        });

        group.MapPost("/{prefix}/namespaces/{namespace}/tables/{table}/credentials", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            [FromServices] IIcebergRestCatalogFederationService catalogService,
            CancellationToken ct) =>
        {
            var tenant = RequireTenant(context);
            try
            {
                var credential = await catalogService.VendCredentialAsync(tenant, @namespace, table, context.User, ct);
                return Results.Ok(credential);
            }
            catch (TransportNotAllowedException ex)
            {
                return HandleIcebergError(context, ex);
            }
            catch (SecurityException ex)
            {
                return HandleIcebergError(context, ex);
            }
            catch (NotSupportedException ex)
            {
                return HandleIcebergError(context, ex);
            }
        });
    }

    /// <summary>Iceberg REST <c>ErrorModel</c>with a fixed message per status; the exception is logged, never returned.</summary>
    internal static IResult HandleIcebergError(HttpContext context, Exception ex)
    {
        var (status, code, message) = ex switch
        {
            TransportNotAllowedException => (StatusCodes.Status403Forbidden, "TRANSPORT_NOT_ALLOWED", "This data source is not exposed on the requested transport."),
            SecurityException or UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "ACCESS_DENIED", "Access to the requested Iceberg resource was denied."),
            ArgumentException or FormatException or InvalidDataException => (StatusCodes.Status400BadRequest, "BAD_REQUEST", "The Iceberg catalog request is invalid."),
            NotSupportedException => (StatusCodes.Status501NotImplemented, "NOT_IMPLEMENTED", "The requested Iceberg catalog operation is not supported."),
            _ => (StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "An internal error occurred.")
        };

        LogFailure(context, status, code, ex);
        return Results.Json(new { error = new { message, type = code, code = status } }, statusCode: status);
    }

    internal static string RequireTenant(HttpContext context)
    {
        var tenant = GetRequestTenant(context);
        if (string.IsNullOrWhiteSpace(tenant.Value))
        {
            throw new TenantUnresolvedException();
        }

        return tenant.Value;
    }

    private static TenantId GetRequestTenant(HttpContext context)
    {
        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item)
            && item is SecurityPrincipalContext principalCtx
            && !string.IsNullOrWhiteSpace(principalCtx.TenantId.Value))
        {
            return principalCtx.TenantId;
        }

        if (context.Items.TryGetValue(SecurityPrincipalContext.TenantIdItemKey, out var raw)
            && raw is TenantId stamped)
        {
            return stamped;
        }

        return context.User.GetTenantId();
    }

    private static void LogFailure(HttpContext? context, int status, string code, Exception exception)
    {
        var logger = context?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Autheris.Api.Errors.ApiProblems");
        if (logger is null)
        {
            return;
        }

        var traceId = context?.TraceIdentifier ?? string.Empty;
        if (status >= 500)
        {
            logger.LogError(exception, "API request failed with status {Status} ({Code}), traceId={TraceId}.", status, code, traceId);
        }
        else
        {
            logger.LogWarning(exception, "API request rejected with status {Status} ({Code}), traceId={TraceId}.", status, code, traceId);
        }
    }
}
