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

            var namespaces = await catalogService.ListNamespacesAsync(tenantId, context.RequestAborted);
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

            var tables = await catalogService.ListTablesAsync(tenantId, @namespace, context.RequestAborted);
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
            catch (System.Collections.Generic.KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (SecurityException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
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
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status501NotImplemented);
            }
            catch (SecurityException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequireAuthorization();

        return app;
    }
}
