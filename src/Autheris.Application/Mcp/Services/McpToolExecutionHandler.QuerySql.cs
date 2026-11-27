namespace Autheris.Application.Mcp.Services;

using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Application.Tenancy;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class McpToolExecutionHandler
{
    private async Task<string> ExecuteQuerySqlAsync(
        McpToolDefinition tool,
        JsonElement root,
        McpSessionContext sessionContext,
        ClaimsPrincipal userPrincipal,
        TenantId tenantId,
        CancellationToken ct)
    {
        var sql = root.TryGetProperty("sql", out var sqlProp) && sqlProp.ValueKind == JsonValueKind.String ? sqlProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(sql))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing required argument 'sql'.");
        }

        string? dataSource = null;
        if (root.TryGetProperty("dataSource", out var dataSourceProp) && dataSourceProp.ValueKind != JsonValueKind.Null)
        {
            if (dataSourceProp.ValueKind != JsonValueKind.String)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'dataSource' must be a string.");
            }

            dataSource = dataSourceProp.GetString();
        }

        var webSqlOptions = _gatewayOptions?.WebSql ?? new WebSqlOptions();
        var maxAllowedRows = Autheris.Application.Sql.SqlRowLimit.For(webSqlOptions).MaxAllowedRows;
        if (maxAllowedRows <= 0)
        {
            maxAllowedRows = Autheris.Application.Sql.SqlRowLimit.HardMaxRows;
        }

        int? requestedSize = null;
        var pageGiven = false;
        if (root.TryGetProperty("page", out var pageProp) && pageProp.ValueKind != JsonValueKind.Null)
        {
            if (pageProp.ValueKind != JsonValueKind.Object)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'page' must be an object.");
            }

            pageGiven = true;
            if (!pageProp.TryGetProperty("size", out var sizeProp)
                || sizeProp.ValueKind != JsonValueKind.Number
                || !sizeProp.TryGetInt32(out var parsedSize)
                || parsedSize < 1
                || parsedSize > maxAllowedRows)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Page size must be between 1 and the WebSQL maximum page size (WebSql:MaxAllowedRows).");
            }

            requestedSize = parsedSize;
        }

        string? next = null;
        if (root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind != JsonValueKind.Null)
        {
            if (nextProp.ValueKind != JsonValueKind.String)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'next' must be a string.");
            }

            next = nextProp.GetString();
            if (string.IsNullOrWhiteSpace(next) || next.Length > 512)
            {
                return McpToolError.FailSql(new SqlPagingTokenInvalidException()).ContentJson;
            }
        }

        if (!McpSqlParameters.TryRead(root, out var parameters))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, McpSqlParameters.InvalidMessage);
        }

        // The executed page size: never above Mcp:MaxResultRows, the WebSQL maximum or the scope clamp, and never above what was asked.
        var cap = McpRowCap.ResolveCap(_gatewayOptions?.Mcp?.MaxResultRows ?? McpRowCap.DefaultCap);
        if (maxAllowedRows < cap)
        {
            cap = (int)maxAllowedRows;
        }

        bool hasSqlScope = Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var activeSqlScope);
        if (hasSqlScope && cap > 0)
        {
            cap = activeSqlScope.ClampRows(cap);
        }

        var executedSize = Math.Min(requestedSize ?? cap, cap);
        var sizeReduced = requestedSize is { } asked && executedSize < asked;
        var boundedRowLimit = Autheris.Application.Sql.SqlRowLimit.For(webSqlOptions).CappedAt(cap);
        if (hasSqlScope && activeSqlScope.MaxResultRows is > 0)
        {
            boundedRowLimit = activeSqlScope.ClampRowLimit(boundedRowLimit);
        }

        var paging = pageGiven || next is not null;
        try
        {
            GovernedSqlResult sqlResult;
            var pagedRequest = new GovernedSqlQueryRequest(
                sql,
                Parameters: parameters,
                DataSourceName: dataSource,
                RowLimit: boundedRowLimit,
                Transport: SqlTransports.Mcp,
                Page: new SqlPageWindow(0, executedSize, 1),
                NextToken: next);
            var fallbackTruncated = false;
            try
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(pagedRequest, userPrincipal, tenantId, ct).ConfigureAwait(false);
            }
            catch (SqlPagingUnsupportedException) when (!paging)
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    new GovernedSqlQueryRequest(sql, Parameters: parameters, DataSourceName: dataSource, RowLimit: boundedRowLimit, Transport: SqlTransports.Mcp),
                    userPrincipal,
                    tenantId,
                    ct).ConfigureAwait(false);
                fallbackTruncated = sqlResult.Truncated && cap < maxAllowedRows;
            }

            McpResultMasking.Report(sqlResult.HasPolicyMaskedValues);
            McpRowContinuation.Offer(sqlResult.ResumeAfter);

            // No later trim exists: a result above the executed size is cut here and no continuation is offered.
            if (sqlResult.Rows.Count > executedSize)
            {
                sqlResult = sqlResult with
                {
                    Rows = sqlResult.Rows.Take(executedSize).ToList().AsReadOnly(),
                    RowCount = executedSize,
                    Truncated = true,
                    Next = null,
                    ResumeAfter = null
                };
            }

            if (sizeReduced || fallbackTruncated)
            {
                McpRowContinuation.ReportPageSizeCapped();
            }

            return Autheris.Application.Sql.WebSqlResultJson.Serialize(sqlResult);
        }
        catch (Exception ex) when (ex is SqlPagingTokenInvalidException or SqlPagingUnsupportedException)
        {
            return McpToolError.FailSql(ex).ContentJson;
        }
        catch (ArgumentException ex) when (!SqlCompilerErrorMapper.TryMap(ex, out _))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, McpSqlParameters.InvalidValueMessage);
        }
        catch (UnauthorizedAccessException)
        {
            return McpToolError.Serialize(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.PolicyDenied, string.Empty);
        }
    }
}
