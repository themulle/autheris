namespace Autheris.Api.Endpoints;

using System.Linq;
using System.Security;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// F-DATA-05: Apache Iceberg REST Catalog (IRC) RFC endpoints with Dynamic STS Credential Vending.
/// </summary>
public static class IcebergRestCatalogEndpoints
{
    public static IEndpointRouteBuilder MapIcebergRestCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithIngressTransport(Autheris.Domain.Options.DataSources.DataSourceTransportFlags.Rest);

        // GET /v1/{prefix}/namespaces
        group.MapGet("/v1/{prefix}/namespaces", async (
            string prefix,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = RequireTenant(context);

            var namespaces = await catalogService.ListNamespacesAsync(tenantId, context.User, context.RequestAborted);
            var response = new IcebergListNamespacesResponse(namespaces.Select(ns => (IReadOnlyList<string>)new[] { ns }).ToList());
            return Results.Ok(response);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // GET /v1/{prefix}/namespaces/{namespace}/tables
        group.MapGet("/v1/{prefix}/namespaces/{namespace}/tables", async (
            string prefix,
            string @namespace,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = RequireTenant(context);

            var tables = await catalogService.ListTablesAsync(tenantId, @namespace, context.User, context.RequestAborted);
            var response = new IcebergListTablesResponse(
                tables.Select(t => new IcebergRestTableIdentifier(new[] { @namespace }, t)).ToList());
            return Results.Ok(response);
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // GET /v1/{prefix}/namespaces/{namespace}/tables/{table}
        group.MapGet("/v1/{prefix}/namespaces/{namespace}/tables/{table}", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = RequireTenant(context);

            try
            {
                var tableResponse = await catalogService.LoadTableAsync(tenantId, @namespace, table, context.User, context.RequestAborted);
                return Results.Ok(tableResponse);
            }
            catch (SecurityException ex)
            {
                // Wunsch 9: unknown, inactive and denied tables all surface as SecurityException (403).
                return HandleIcebergError(context, ex);
            }
            catch (Autheris.Domain.Exceptions.TransportNotAllowedException ex)
            {
                return HandleIcebergError(context, ex);
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        // POST /v1/{prefix}/namespaces/{namespace}/tables/{table}/credentials
        group.MapPost("/v1/{prefix}/namespaces/{namespace}/tables/{table}/credentials", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = RequireTenant(context);

            try
            {
                var credential = await catalogService.VendCredentialAsync(tenantId, @namespace, table, context.User, context.RequestAborted);
                return Results.Ok(credential);
            }
            catch (NotSupportedException ex)
            {
                return HandleIcebergError(context, ex);
            }
            catch (SecurityException ex)
            {
                return HandleIcebergError(context, ex);
            }
            catch (Autheris.Domain.Exceptions.TransportNotAllowedException ex)
            {
                return HandleIcebergError(context, ex);
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    /// <summary>Iceberg REST <c>ErrorModel</c>with a fixed message per status; the exception is logged, never returned.</summary>
    internal static IResult HandleIcebergError(HttpContext context, Exception ex)
    {
        var (status, code, message) = ex switch
        {
            SecurityException => (StatusCodes.Status403Forbidden, ApiErrorCodes.AccessDenied, "Access denied."),
            Autheris.Domain.Exceptions.TransportNotAllowedException => (StatusCodes.Status403Forbidden, ApiErrorCodes.TransportNotAllowed, "Access to the data source is not allowed over this transport."),
            ArgumentException => (StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest, "The request is invalid."),
            NotSupportedException => (StatusCodes.Status501NotImplemented, ApiErrorCodes.NotImplemented, "Not implemented."),
            _ => (StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, "An unexpected error occurred.")
        };
        ApiProblems.LogFailure(context, ex, code);
        return Results.Json(new { error = new { message, type = code, code = status } }, statusCode: status);
    }

    // DT-A-9: no literal tenant fallback; a request without a resolvable tenant is refused with 403 TENANT_UNRESOLVED.
    internal static string RequireTenant(HttpContext context)
    {
        var tenant = EndpointSecurity.GetRequestTenant(context);
        return string.IsNullOrWhiteSpace(tenant.Value)
            ? throw new Autheris.Domain.Exceptions.TenantUnresolvedException()
            : tenant.Value;
    }
}
