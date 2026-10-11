namespace Autheris.Api.Endpoints;

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Apache.Arrow.Ipc;
using Autheris.Api.Extensions;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Errors;
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
            catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out _))
            {
                return ErrorResult(context, ex);
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
            catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out _))
            {
                return ErrorResult(context, ex);
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
            // leaveOpen: disposing the stream would complete the body writer before an error answer could be written.
            var stream = context.Response.BodyWriter.AsStream(leaveOpen: true);

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
        catch (Exception ex) when (SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            return ErrorResult(context, ex);
        }
        catch (ArgumentException)
        {
            return BadRequestResult();
        }
    }

    // RR-L3-03: parser and database details stay in the server log.
    private static IResult BadRequestResult() =>
        Results.Problem(detail: WebSqlEndpoints.GenericBadRequestMessage, statusCode: StatusCodes.Status400BadRequest);

    // WP-X0a (SEC-X1-01): Flight is HTTP. The response carries the mapped status, the fixed message and the stable `code`; never Exception.Message, in any environment.
    private static IResult ErrorResult(HttpContext context, Exception ex)
    {
        SqlCompilerErrorMapper.TryMap(ex, out var mapped);
        SqlCompilerErrorMapper.RecordOutcome(mapped);
        var extensions = new Dictionary<string, object?> { ["code"] = mapped.Code };
        if (mapped.Limit is not null)
        {
            extensions["limit"] = mapped.Limit;
        }

        return Results.Problem(detail: mapped.Title, statusCode: mapped.Status, extensions: extensions);
    }
}
