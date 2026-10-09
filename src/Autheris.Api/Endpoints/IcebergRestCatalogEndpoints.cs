namespace Autheris.Api.Endpoints;

using System.Linq;
using System.Security;
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
        // GET /v1/{prefix}/namespaces
        app.MapGet("/v1/{prefix}/namespaces", async (
            string prefix,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";

            var namespaces = await catalogService.ListNamespacesAsync(tenantId, context.User, context.RequestAborted);
            var response = new IcebergListNamespacesResponse(namespaces.Select(ns => (IReadOnlyList<string>)new[] { ns }).ToList());
            return Results.Ok(response);
        }).RequireAuthorization();

        // GET /v1/{prefix}/namespaces/{namespace}/tables
        app.MapGet("/v1/{prefix}/namespaces/{namespace}/tables", async (
            string prefix,
            string @namespace,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";

            var tables = await catalogService.ListTablesAsync(tenantId, @namespace, context.User, context.RequestAborted);
            var response = new IcebergListTablesResponse(
                tables.Select(t => new IcebergRestTableIdentifier(new[] { @namespace }, t)).ToList());
            return Results.Ok(response);
        }).RequireAuthorization();

        // GET /v1/{prefix}/namespaces/{namespace}/tables/{table}
        app.MapGet("/v1/{prefix}/namespaces/{namespace}/tables/{table}", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";

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
        }).RequireAuthorization();

        // POST /v1/{prefix}/namespaces/{namespace}/tables/{table}/credentials
        app.MapPost("/v1/{prefix}/namespaces/{namespace}/tables/{table}/credentials", async (
            string prefix,
            string @namespace,
            string table,
            HttpContext context,
            IIcebergRestCatalogFederationService catalogService) =>
        {
            var tenantId = EndpointSecurity.GetRequestTenant(context).Value;
            if (string.IsNullOrWhiteSpace(tenantId)) tenantId = "default";

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
        }).RequireAuthorization();

        return app;
    }

    private static IResult HandleIcebergError(HttpContext context, Exception ex)
    {
        var isProduction = context.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.IsProduction() ?? false;
        return ex switch
        {
            SecurityException => Results.Problem(detail: isProduction ? "Access denied." : ex.Message, statusCode: StatusCodes.Status403Forbidden),
            ArgumentException => Results.Problem(detail: isProduction ? "Access denied." : ex.Message, statusCode: StatusCodes.Status400BadRequest),
            NotSupportedException => Results.Problem(detail: isProduction ? "Not implemented." : ex.Message, statusCode: StatusCodes.Status501NotImplemented),
            _ => Results.Problem(detail: isProduction ? "An unexpected error occurred." : ex.Message, statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}
