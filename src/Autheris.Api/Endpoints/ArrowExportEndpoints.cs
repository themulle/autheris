namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-DATA-04: HTTP endpoints for streaming governed relational data directly in Apache Arrow IPC columnar format.
/// </summary>
public static class ArrowExportEndpoints
{
    public sealed record ArrowExportPayload(
        string Table,
        string? Sql = null);

    public static IEndpointRouteBuilder MapArrowExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/v1/export/arrow", HandleArrowExportAsync)
            .WithName("ExportTableToArrow")
            .WithSummary("Exports governed tabular rows directly into Apache Arrow IPC streaming format")
            .RequireRebac("viewer", "table", paramName: "table", source: RebacParameterSource.Query);

        return endpoints;
    }

    internal static async Task<IResult> HandleArrowExportAsync(
        HttpContext httpContext,
        IArrowExportService arrowService,
        IGovernedSqlExecutionService? sqlExecutionService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(arrowService);

        // 1. Resolve Identity & Tenant
        var caller = EndpointSecurity.GetCallerIdentity(httpContext);
        if (string.IsNullOrWhiteSpace(caller))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Authentication required for Arrow export.");
        }

        var tenant = EndpointSecurity.GetRequestTenant(httpContext);

        // 2. Resolve Table or SQL from Query or Body
        string? table = httpContext.Request.Query["table"].ToString();
        string? sql = null;

        if (string.IsNullOrWhiteSpace(table))
        {
            try
            {
                var body = await httpContext.Request.ReadFromJsonAsync<ArrowExportPayload>(cancellationToken: ct).ConfigureAwait(false);
                if (body != null)
                {
                    table = body.Table;
                    sql = body.Sql;
                }
            }
            catch
            {
                // Fall-through
            }
        }

        if (string.IsNullOrWhiteSpace(table) && string.IsNullOrWhiteSpace(sql))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request",
                detail: "Target table parameter or SQL query is required.");
        }

        if (!string.IsNullOrWhiteSpace(table))
        {
            try
            {
                var norm = TableIdentifierNormalizer.Normalize(table);
                table = norm.ToQualifiedName();
            }
            catch
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad Request",
                    detail: $"Invalid table identifier '{table}'.");
            }
        }

        var effectiveSql = !string.IsNullOrWhiteSpace(sql) ? sql : $"SELECT * FROM {table}";

        // 3. Execute Governed Query (RLS + Masking + AST Security)
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = Array.Empty<IReadOnlyDictionary<string, object?>>();
        if (sqlExecutionService == null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable",
                detail: "Governed SQL execution is not available.");
        }

        try
        {
            var queryRequest = new GovernedSqlQueryRequest(effectiveSql);
            var queryResult = await sqlExecutionService.ExecuteQueryBufferedAsync(queryRequest, httpContext.User, tenant, ct).ConfigureAwait(false);
            rows = queryResult.Rows;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // RR-L3-03: never echo exception, parser or database messages (table/column oracle, backend details).
            var logger = httpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger("Autheris.Api.Endpoints.ArrowExport");
            var traceId = httpContext.TraceIdentifier;
            switch (ex)
            {
                case System.Security.SecurityException secEx:
                    logger?.LogWarning(secEx, "Arrow export rejected by security policy. TraceId={TraceId}", traceId);
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden",
                        detail: secEx is Autheris.Application.Sql.WebSqlPolicyException ? secEx.Message : WebSqlEndpoints.GenericForbiddenMessage,
                        extensions: new Dictionary<string, object?> { ["traceId"] = traceId });
                case ArgumentException argEx:
                    logger?.LogWarning(argEx, "Arrow export bad request. TraceId={TraceId}", traceId);
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Bad Request",
                        detail: WebSqlEndpoints.GenericBadRequestMessage,
                        extensions: new Dictionary<string, object?> { ["traceId"] = traceId });
                default:
                    logger?.LogError(ex, "Arrow export failed. TraceId={TraceId}", traceId);
                    return Results.Problem(
                        statusCode: StatusCodes.Status500InternalServerError,
                        title: "Internal Server Error",
                        detail: "The export could not be completed.",
                        extensions: new Dictionary<string, object?> { ["traceId"] = traceId });
            }
        }

        // 4. Stream Apache Arrow IPC binary payload
        var bytes = await arrowService.ExportToBytesAsync(rows, ct).ConfigureAwait(false);
        var sanitizedName = !string.IsNullOrWhiteSpace(table)
            ? System.Text.RegularExpressions.Regex.Replace(table, @"[^a-zA-Z0-9_\-]", "_")
            : "export";
        var fileName = $"{sanitizedName}.arrow";

        return Results.File(
            bytes,
            contentType: IArrowExportService.ArrowStreamContentType,
            fileDownloadName: fileName);
    }
}
