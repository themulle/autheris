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
        var query = root.TryGetProperty("query", out var qProp) ? qProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Missing required argument 'query'.");
        }

        int? requestedLimit = null;
        if (root.TryGetProperty("limit", out var limitProp) && limitProp.ValueKind != JsonValueKind.Null)
        {
            if (limitProp.ValueKind != JsonValueKind.Number || !limitProp.TryGetInt32(out var parsedLimit) || parsedLimit < 1)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'limit' must be a positive integer.");
            }

            requestedLimit = parsedLimit;
        }

        string? cursor = null;
        if (root.TryGetProperty("cursor", out var cursorProp) && cursorProp.ValueKind != JsonValueKind.Null)
        {
            if (cursorProp.ValueKind != JsonValueKind.String)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid continuation cursor.");
            }

            cursor = cursorProp.GetString();
            if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid continuation cursor.");
            }
        }

        if (!McpSqlParameters.TryRead(root, out var parameters))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, McpSqlParameters.InvalidMessage);
        }

        // The optional structured view narrows and sorts the result of the fixed statement; a refused view never reaches the pipeline.
        Autheris.Application.Sql.View.WebSqlViewPlan? viewPlan = null;
        if (root.TryGetProperty("view", out var viewProp) && viewProp.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            try
            {
                viewPlan = Autheris.Application.Sql.View.WebSqlViewComposer.Plan(query, viewProp, ct);
            }
            catch (Autheris.Application.Sql.View.WebSqlViewException ex)
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, ex.UserMessage);
            }
        }

        GovernedSqlQueryRequest WithView(GovernedSqlQueryRequest request) => viewPlan is null ? request : viewPlan.Apply(request);

        var effectiveLimit = McpRowCap.Effective(
            requestedLimit,
            McpRowCap.ResolveCap(_gatewayOptions?.Mcp?.MaxResultRows ?? McpRowCap.DefaultCap));
        if (_gatewayOptions?.WebSql?.MaxAllowedRows is > 0 and var maxAllowedRows && maxAllowedRows < effectiveLimit)
        {
            effectiveLimit = (int)maxAllowedRows;
        }
        bool hasSqlScope = Autheris.Application.Common.Quota.ScopeQueryExecutionGuard.TryGetCurrent(out var activeSqlScope);
        if (hasSqlScope && effectiveLimit > 0)
        {
            effectiveLimit = activeSqlScope.ClampRows(effectiveLimit);
        }

        var pageWindow = cursor is null || requestedLimit.HasValue
            ? new SqlPageWindow(0, effectiveLimit, 1)
            : null;
        var boundedRowLimit = Autheris.Application.Sql.SqlRowLimit.For(_gatewayOptions?.WebSql ?? new WebSqlOptions())
            .CappedAt(effectiveLimit);
        if (hasSqlScope && activeSqlScope.MaxResultRows is > 0)
        {
            boundedRowLimit = activeSqlScope.ClampRowLimit(boundedRowLimit);
        }

        try
        {
            // The count is its own governed statement over the same fixed statement and filter (never the page window).
            long? totalCount = null;
            if (viewPlan?.ApplyCount(new GovernedSqlQueryRequest(query, RowLimit: boundedRowLimit, Transport: SqlTransports.Mcp, Parameters: parameters)) is { } countRequest)
            {
                var countResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(countRequest, userPrincipal, tenantId, ct).ConfigureAwait(false);
                totalCount = countResult.Rows.Count == 1 && countResult.Rows[0].Count == 1
                    ? Convert.ToInt64(countResult.Rows[0].Values.First(), System.Globalization.CultureInfo.InvariantCulture)
                    : throw new InvalidOperationException("The total count statement returned no single value.");
            }

            GovernedSqlResult sqlResult;
            try
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    WithView(new GovernedSqlQueryRequest(
                        query,
                        RowLimit: boundedRowLimit,
                        Transport: SqlTransports.Mcp,
                        Page: pageWindow,
                        NextToken: cursor,
                        Parameters: parameters)),
                    userPrincipal,
                    tenantId,
                    ct).ConfigureAwait(false);
            }
            catch (SqlPagingUnsupportedException) when (cursor is null && requestedLimit is null)
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    WithView(new GovernedSqlQueryRequest(query, RowLimit: boundedRowLimit, Transport: SqlTransports.Mcp, Parameters: parameters)),
                    userPrincipal,
                    tenantId,
                    ct).ConfigureAwait(false);
            }

            McpResultMasking.Report(sqlResult.HasPolicyMaskedValues);
            McpRowContinuation.Offer(sqlResult.ResumeAfter);
            return JsonSerializer.Serialize(McpSqlQueryResult.From(sqlResult, totalCount), JsonOpts);
        }
        catch (SqlPagingTokenInvalidException)
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Invalid or expired continuation cursor.");
        }
        catch (SqlPagingUnsupportedException)
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "Cursor pagination is not supported for this query.");
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
