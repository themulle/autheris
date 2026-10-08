namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
            .RequireRebac("viewer", "table", paramName: "table", source: RebacParameterSource.QueryOrJsonBody);

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

        // WebSQL findings 4.3: the route filter checks the table named in the query or body. SQL from the body may read
        // other tables, so every table it references needs the same relation (fail-closed when it cannot be analyzed).
        if (!string.IsNullOrWhiteSpace(sql) &&
            !await IsSqlPermittedByRebacAsync(httpContext, sql, caller, tenant, ct).ConfigureAwait(false))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Access denied by ReBAC policy.");
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
            // Row limits of the Arrow transport (Gateway:RowLimits:ArrowExport, falling back to WebSql).
            var gatewayOptions = httpContext.RequestServices?.GetService<IOptions<GatewayOptions>>()?.Value;
            var queryRequest = new GovernedSqlQueryRequest(
                effectiveSql,
                RowLimit: SqlRowLimit.For(gatewayOptions?.WebSql ?? new WebSqlOptions(), gatewayOptions?.RowLimits?.ArrowExport));
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

    /// <summary>
    /// True when ReBAC is disabled or the caller holds the "viewer" relation on every table <paramref name="sql"/>
    /// references. A statement that cannot be analyzed, or references no table, is not permitted.
    /// </summary>
    private static async Task<bool> IsSqlPermittedByRebacAsync(HttpContext httpContext, string sql, string caller, TenantId tenant, CancellationToken ct)
    {
        var services = httpContext.RequestServices;
        var rebacOptions = services?.GetService<IOptions<GatewayOptions>>()?.Value?.Rebac;
        if (rebacOptions is not null && !rebacOptions.Enabled)
        {
            return true;
        }

        var engine = services?.GetService<TrinoSqlEngine.ISqlEngine>();
        var loader = services?.GetService<IRebacBatchDataLoader>();
        if (engine == null || loader == null)
        {
            return false;
        }

        IReadOnlyList<TrinoSqlEngine.Analysis.TableAccessTarget> referencedTables;
        try
        {
            referencedTables = engine.Analyze(sql.AsMemory()).ReferencedTables;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (referencedTables.Count == 0)
        {
            return false;
        }

        foreach (var target in referencedTables)
        {
            if (!TableIdentifierNormalizer.TryNormalize(target.FullName, out var normalized) ||
                !await loader.CheckAsync(tenant.Value, caller, "viewer", "table:" + normalized.ToQualifiedName(), ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }
}
