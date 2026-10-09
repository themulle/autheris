namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Apache.Arrow.Ipc;
using Autheris.Api.Extensions;
using Autheris.Application.Serialization;
using Autheris.Domain.Audit;
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
            catch (ArgumentException)
            {
                return BadRequestResult();
            }
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

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
        }).RequireAuthorization().WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        app.MapPost("/api/v1/flight/sql/stream", HandleStreamAsync).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    internal static async Task<IResult> HandleStreamAsync(FlightSqlTicket ticket, HttpContext context, IArrowFlightSqlServer server)
    {
        var auditContext = context.Features.Get<AuditContext>();
        try
        {
            context.Response.ContentType = "application/vnd.apache.arrow.stream";
            await using var stream = context.Response.BodyWriter.AsStream();

            ArrowStreamWriter? writer = null;
            long totalRows = 0;
            await foreach (var batch in server.DoGetStreamAsync(ticket, context.User, EndpointSecurity.GetRequestTenant(context), context.RequestAborted))
            {
                totalRows += batch.Length;
                auditContext?.RecordMetrics(totalRows, null);

                if (writer == null)
                {
                    // Same signal as the WebSQL JSON and Parquet paths: the row limit cut the result. A schema without
                    // metadata has Metadata == null in Apache.Arrow.
                    if (batch.Schema.Metadata?.TryGetValue(ArrowFlightSqlServer.TruncatedMetadataKey, out var truncated) == true && truncated == "true")
                    {
                        context.Response.Headers["X-Autheris-Truncated"] = "true";
                    }

                    writer = new ArrowStreamWriter(stream, batch.Schema, leaveOpen: true);
                }

                await writer.WriteRecordBatchAsync(batch, context.RequestAborted);
            }

            return Results.Empty;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            auditContext?.Error("CLIENT_ABORTED", AuditEventTypes.QueryExecutionError);
            throw;
        }
        catch (SecurityException ex)
        {
            return ForbidResult(context, ex);
        }
        catch (ArgumentException)
        {
            return BadRequestResult();
        }
    }

    // RR-L3-03: parser and database details stay in the server log.
    private static IResult BadRequestResult() =>
        Results.Problem(detail: WebSqlEndpoints.GenericBadRequestMessage, statusCode: StatusCodes.Status400BadRequest);

    private static IResult ForbidResult(HttpContext context, SecurityException ex)
    {
        var isProduction = context.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.IsProduction() ?? false;
        return Results.Problem(detail: isProduction ? "Access denied." : ex.Message, statusCode: StatusCodes.Status403Forbidden);
    }
}
