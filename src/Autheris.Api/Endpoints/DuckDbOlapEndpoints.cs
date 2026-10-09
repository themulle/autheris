using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Autheris.Api.Extensions;
using Autheris.Domain.Audit;
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
           .WithTags("Analytics & OLAP")
           .WithAudit(AuditLevel.Full, AuditEventTypes.TableQuery);

        return app;
    }

    internal static async Task HandleOlapQueryAsync(
        HttpContext httpContext,
        IDuckDbOlapEngine olapEngine,
        ITableMetadataRepository metadataRepository,
        IAutherisConnectorRegistry connectorRegistry,
        ICrossDomainAccessResolver accessResolver,
        IColumnMaskingProvider maskingProvider,
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
            !gatewayOptions.Value.IsQuickstartProfile)
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

        // SEC D-4: every staged table and every query is audited BEFORE data is returned; an audit failure fails the request closed.
        var auditRepo = httpContext.RequestServices?.GetService<IAuditLogRepository>();
        var actorSid = secContext.UserSid;
        var stagedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sqlHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dto.Sql)));

        async Task<bool> TryAuditAsync(string eventType, string targetTable, object details)
        {
            if (auditRepo == null)
            {
                logger.LogError("No audit repository available; OLAP request is rejected (fail-closed).");
                return false;
            }

            try
            {
                await auditRepo.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = eventType,
                    ActorSid = actorSid,
                    TargetTable = targetTable,
                    Decision = "ALLOW",
                    TraceId = httpContext.TraceIdentifier,
                    DetailsJson = JsonSerializer.Serialize(details)
                }, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OLAP audit write failed; request is rejected (fail-closed).");
                return false;
            }
        }

        async Task WriteAuditFailureAsync()
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Audit unavailable; the analytical request was rejected." }, ct);
        }

        var sources = new List<OlapTableSource>();

        if (dto.TableNames != null && dto.TableNames.Count > 0)
        {
            int maxTableCount = options.MaxTableCount > 0 ? options.MaxTableCount : 10;
            var distinctTableNames = dto.TableNames
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctTableNames.Count > maxTableCount)
            {
                logger.LogWarning("OLAP request exceeded maximum table count: {Count} > {Max}", distinctTableNames.Count, maxTableCount);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = $"OLAP request exceeds maximum allowed distinct table count of {maxTableCount}." }, ct);
                return;
            }

            int totalStagedRows = 0;
            int maxTotalStagedRows = options.MaxTotalStagedRows > 0 ? options.MaxTotalStagedRows : 500000;

            foreach (var rawTableName in distinctTableNames)
            {
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

                // SEC-OLAP-05 / RR-L3-04 / Architecture 1: one access decision (ReBAC whenever enabled, consents, Casbin, RLS)
                // without leaking internal policy details.
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
                    Arguments: new Dictionary<string, object?> { ["limit"] = options.MaxStagedRowsPerTable + 1 },
                    PushdownFilterSql: decision.CombinedRowFilterSql,
                    Limit: options.MaxStagedRowsPerTable + 1,
                    Offset: 0);

                // Architecture 2 / SQL2-5: the governed reader applies the row filter when the connector did not push it
                // down, masks exactly once (SQL2-4) and checks the staging capacity before rows are kept (SQL2-2).
                List<IReadOnlyDictionary<string, object?>> maskedRows;
                try
                {
                    var read = await GovernedConnectorReader.ReadAsync(
                        connector,
                        session,
                        meta,
                        new GovernedRowPolicy(
                            maskingProvider,
                            gatewayOptions.Value.DataMasking?.HmacKeyId,
                            MaskingDisabled: gatewayOptions.Value.IsColumnMaskingDisabled,
                            MaxRows: options.MaxStagedRowsPerTable),
                        ct).ConfigureAwait(false);
                    maskedRows = read.Rows.ToList();
                }
                catch (ConnectorRowLimitExceededException)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await httpContext.Response.WriteAsJsonAsync(new { error = $"Table '{meta.Identifier}' exceeds maximum allowed staging rows ({options.MaxStagedRowsPerTable})." }, ct);
                    return;
                }

                totalStagedRows += maskedRows.Count;
                if (totalStagedRows > maxTotalStagedRows)
                {
                    logger.LogWarning("OLAP request exceeded total staged rows limit across tables: {Total} > {Max}", totalStagedRows, maxTotalStagedRows);
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await httpContext.Response.WriteAsJsonAsync(new { error = $"Total staged rows across all tables ({totalStagedRows}) exceeded maximum allowed limit ({maxTotalStagedRows})." }, ct);
                    return;
                }

                if (!await TryAuditAsync("OLAP_TABLE_STAGED", meta.Identifier.ToQualifiedName(), new
                {
                    tenant = tenantId.Value,
                    sid = actorSid.Value,
                    table = meta.Identifier.ToQualifiedName(),
                    rowCount = maskedRows.Count,
                    sqlSha256 = sqlHash
                }).ConfigureAwait(false))
                {
                    await WriteAuditFailureAsync();
                    return;
                }

                stagedCounts[meta.Identifier.ToQualifiedName()] = maskedRows.Count;
                sources.Add(new OlapTableSource(meta.Identifier, maskedRows, meta));
            }
        }

        try
        {
            var queryRequest = new OlapQueryRequest(dto.Sql, sources, dto.Limit);
            var result = await olapEngine.ExecuteOlapQueryAsync(queryRequest, ct).ConfigureAwait(false);

            if (!await TryAuditAsync("OLAP_QUERY", string.Join(",", stagedCounts.Keys), new
            {
                tenant = tenantId.Value,
                sid = actorSid.Value,
                tables = stagedCounts,
                stagedRowCount = stagedCounts.Values.Sum(),
                resultRowCount = result.Rows.Count,
                totalRows = result.TotalRowCount,
                sqlSha256 = sqlHash
            }).ConfigureAwait(false))
            {
                await WriteAuditFailureAsync();
                return;
            }

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
        catch (SecurityException ex) when (ex.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "OLAP engine concurrency limit reached.");
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await httpContext.Response.WriteAsJsonAsync(new { error = "OLAP capacity limit reached. Please retry later." }, ct);
        }
        catch (SecurityException ex) when (ex.Message.Contains("memory size", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(ex, "OLAP query result byte budget exceeded.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = isDev ? ex.Message : "OLAP query result exceeded maximum allowed memory size." }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing DuckDB OLAP query.");
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            if (isDev || gatewayOptions.Value.IsQuickstartProfile)
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
