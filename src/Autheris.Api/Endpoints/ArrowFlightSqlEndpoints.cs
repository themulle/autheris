namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Apache.Arrow.Ipc;
using Autheris.Application.Serialization;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// F-DATA-04-B: Arrow Flight SQL endpoints for high-throughput columnar analytics.
/// </summary>
public static class ArrowFlightSqlEndpoints
{
    public sealed record FlightSqlQueryRequest(string Query);

    public static IEndpointRouteBuilder MapArrowFlightSqlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/flight/sql/info", async (
            FlightSqlQueryRequest request,
            HttpContext context,
            IArrowFlightSqlServer server) =>
        {
            var tenant = EndpointSecurity.GetRequestTenant(context);
            try
            {
                var info = await server.GetFlightInfoAsync(request.Query, context.User, tenant, context.RequestAborted);
                return Results.Ok(info);
            }
            catch (SecurityException ex)
            {
                return ForbidResult(context, ex);
            }
        }).RequireAuthorization();

        app.MapGet("/api/v1/flight/sql/tables", async (
            string? schema,
            HttpContext context,
            IArrowFlightSqlServer server) =>
        {
            var tenant = EndpointSecurity.GetRequestTenant(context);
            try
            {
                var tables = await server.GetTablesAsync(context.User, tenant, schema, context.RequestAborted);
                return Results.Ok(tables);
            }
            catch (SecurityException ex)
            {
                return ForbidResult(context, ex);
            }
        }).RequireAuthorization();

        app.MapPost("/api/v1/flight/sql/stream", async (
            FlightSqlTicket ticket,
            HttpContext context,
            IArrowFlightSqlServer server) =>
        {
            try
            {
                context.Response.ContentType = "application/vnd.apache.arrow.stream";
                await using var stream = context.Response.BodyWriter.AsStream();

                ArrowStreamWriter? writer = null;
                await foreach (var batch in server.DoGetStreamAsync(ticket, context.User, EndpointSecurity.GetRequestTenant(context), context.RequestAborted))
                {
                    writer ??= new ArrowStreamWriter(stream, batch.Schema, leaveOpen: true);
                    await writer.WriteRecordBatchAsync(batch, context.RequestAborted);
                }

                return Results.Empty;
            }
            catch (SecurityException ex)
            {
                return ForbidResult(context, ex);
            }
        }).RequireAuthorization();

        return app;
    }

    private static IResult ForbidResult(HttpContext context, SecurityException ex)
    {
        var isProduction = context.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.IsProduction() ?? false;
        return Results.Problem(detail: isProduction ? "Access denied." : ex.Message, statusCode: StatusCodes.Status403Forbidden);
    }
}
