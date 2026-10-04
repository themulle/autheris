using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Connectors.Pushdown;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Endpoints;

public sealed record OlapQueryRequestDto(
    string Sql,
    IReadOnlyList<string>? TableNames = null,
    int? Limit = null);

public static class DuckDbOlapEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEndpointRouteBuilder MapDuckDbOlapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/olap/query", HandleOlapQueryAsync)
           .WithName("ExecuteDuckDbOlapQueryV1")
           .WithTags("Analytics & OLAP");

        return app;
    }

    internal static async Task HandleOlapQueryAsync(
        HttpContext httpContext,
        IDuckDbOlapEngine olapEngine,
        ITableMetadataRepository metadataRepository,
        IAutherisConnectorRegistry connectorRegistry,
        ICrossDomainAccessResolver accessResolver,
        IColumnMaskingProvider maskingProvider,
        IRebacEvaluator rebacEvaluator,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.Olap");
        var ct = httpContext.RequestAborted;
        var env = httpContext.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
        bool isDev = env?.IsDevelopment() ?? false;

        var options = gatewayOptions.Value.DuckDbOlap ?? new DuckDbOlapOptions();
        if (!options.Enabled)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await httpContext.Response.WriteAsJsonAsync(new { error = "In-memory DuckDB OLAP engine is currently disabled." }, ct);
            return;
        }

        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true &&
            !string.Equals(gatewayOptions.Value.Profile, "Quickstart", StringComparison.OrdinalIgnoreCase))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Authentication required for analytical OLAP execution." }, ct);
            return;
        }

        OlapQueryRequestDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<OlapQueryRequestDto>(httpContext.Request.Body, JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid JSON payload in OLAP query request.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload in request body." }, ct);
            return;
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Sql))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Missing 'sql' query in request body." }, ct);
            return;
        }

        var secContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = secContext.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Forbidden: Missing tenant identity." }, ct);
            return;
        }

        var sources = new List<OlapTableSource>();

        if (dto.TableNames != null && dto.TableNames.Count > 0)
        {
            foreach (var rawTableName in dto.TableNames)
            {
                if (string.IsNullOrWhiteSpace(rawTableName)) continue;

                TableMetadata? meta = null;
                if (TableIdentifier.TryParse(rawTableName, out var parsedId))
                {
                    meta = await metadataRepository.GetTableMetadataAsync(parsedId, ct).ConfigureAwait(false);
                }
                else
                {
                    var allTables = await metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
                    meta = allTables.FirstOrDefault(t =>
                        string.Equals(t.Identifier.TableName, rawTableName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Identifier.ToQualifiedName(), rawTableName, StringComparison.OrdinalIgnoreCase));
                }

                if (meta == null)
                {
                    logger.LogWarning("OLAP request table '{Table}' not found in metadata catalog.", rawTableName);
                    httpContext.Response.StatusCode = isDev ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
                    await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? $"Table '{rawTableName}' not found in metadata catalog." : $"Access denied to table '{rawTableName}'." }, ct);
                    return;
                }

                var effectiveUser = user ?? new ClaimsPrincipal(new ClaimsIdentity());

                // SEC-OLAP-05 / RR-L3-02: ReBAC Check with canonical qualified table object
                if (rebacEvaluator != null && rebacEvaluator.IsEnabled)
                {
                    var rebacReq = new RebacCheckRequest(
                        secContext.TenantId.Value,
                        secContext.UserSid.Value,
                        "can_query",
                        $"table:{meta.Identifier.ToQualifiedName()}");

                    var rebacDecision = await rebacEvaluator.CheckAsync(rebacReq, ct).ConfigureAwait(false);
                    if (!rebacDecision.Allowed)
                    {
                        logger.LogWarning("ReBAC denied query access to table {Table} for user {User}", meta.Identifier, secContext.UserSid);
                        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? $"Forbidden: Insufficient relationship permissions on table '{meta.Identifier}'." : $"Access denied to table '{rawTableName}'." }, ct);
                        return;
                    }
                }

                // SEC-OLAP-05 / RR-L3-04: ABAC & RLS Resolution without leaking internal policy details
                var decision = await accessResolver.ResolveAccessAsync(effectiveUser, meta.Identifier, meta, tenantId, ct).ConfigureAwait(false);

                if (!decision.IsAllowed)
                {
                    logger.LogWarning("ABAC access denied for table {Table}: {Reasons}", meta.Identifier, string.Join("; ", decision.DeniedReasons));
                    httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? $"Access denied to table '{meta.Identifier}': {string.Join("; ", decision.DeniedReasons)}" : $"Access denied to table '{rawTableName}'." }, ct);
                    return;
                }

                if (!connectorRegistry.TryGetConnectorForTable(meta.Identifier, out var connector) || connector == null)
                {
                    logger.LogWarning("No active connector registered for table {Table}", meta.Identifier);
                    httpContext.Response.StatusCode = isDev ? StatusCodes.Status502BadGateway : StatusCodes.Status403Forbidden;
                    await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? $"No active connector registered for table '{meta.Identifier}'." : $"Access denied to table '{rawTableName}'." }, ct);
                    return;
                }

                var session = new ConnectorSessionContext(
                    Principal: effectiveUser,
                    Tenant: tenantId,
                    AccessDecision: decision,
                    ProjectedColumns: meta.Columns.Select(c => c.ColumnName).ToList(),
                    Arguments: new Dictionary<string, object?> { ["limit"] = options.MaxStagedRowsPerTable },
                    PushdownFilterSql: decision.CombinedRowFilterSql,
                    Limit: options.MaxStagedRowsPerTable,
                    Offset: 0);

                session.Items["TableMetadata"] = meta;

                var splits = await connector.SplitManager.GetSplitsAsync(meta, session, ct).ConfigureAwait(false);
                var rawRows = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var split in splits)
                {
                    var batch = await connector.RecordSource.ReadBatchAsync(split, session, ct).ConfigureAwait(false);
                    // RR-L3-01: verify staging capacity BEFORE adding batch
                    if (rawRows.Count + batch.Count > options.MaxStagedRowsPerTable)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{meta.Identifier}' exceeds maximum allowed staging rows ({options.MaxStagedRowsPerTable})." }, ct);
                        return;
                    }
                    rawRows.AddRange(batch);
                }

                // Dynamic Column Masking preservation (SEC-OLAP-04)
                var maskedRows = rawRows.Select(r => ConnectorRowMasker.MaskRow(r, meta, decision, maskingProvider)).ToList();
                sources.Add(new OlapTableSource(meta.Identifier, maskedRows, meta));
            }
        }

        try
        {
            var queryRequest = new OlapQueryRequest(dto.Sql, sources, dto.Limit);
            var result = await olapEngine.ExecuteOlapQueryAsync(queryRequest, ct).ConfigureAwait(false);

            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                columns = result.Columns,
                rows = result.Rows,
                totalRows = result.TotalRowCount,
                executionDurationMs = result.ExecutionDuration.TotalMilliseconds
            }, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("MaxStagedRowsPerTable", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "Max staged rows limit exceeded in OLAP query.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing DuckDB OLAP query.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            if (isDev || string.Equals(gatewayOptions.Value.Profile, "Quickstart", StringComparison.OrdinalIgnoreCase))
            {
                await httpContext.Response.WriteAsJsonAsync(new { error = "Analytical query execution failed.", details = ex.Message }, ct);
            }
            else
            {
                await httpContext.Response.WriteAsJsonAsync(new { error = "Analytical query execution failed." }, ct);
            }
        }
    }
}
