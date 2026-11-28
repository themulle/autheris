namespace Autheris.Application.Mcp.Services;

using System;
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

        var datasetFacts = false;
        if (root.TryGetProperty("datasetFacts", out var factsProp) && factsProp.ValueKind != JsonValueKind.Null)
        {
            if (factsProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, "'datasetFacts' must be a boolean.");
            }

            datasetFacts = factsProp.ValueKind == JsonValueKind.True;
        }

        if (!McpSqlParameters.TryRead(root, out var parameters))
        {
            return CreateMcpError(sessionContext.TenantId, tool.Name, InvalidParamsCode, McpSqlParameters.InvalidMessage);
        }

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

        IReadOnlyList<Autheris.Application.Sql.Tree.DatasetAccessFacts>? accessFacts = null;
        Action<IReadOnlyList<Autheris.Application.Sql.Tree.DatasetAccessFacts>>? onFacts = datasetFacts ? f => accessFacts = f : null;
        try
        {
            GovernedSqlResult sqlResult;
            try
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    new GovernedSqlQueryRequest(
                        query,
                        RowLimit: boundedRowLimit,
                        Transport: SqlTransports.Mcp,
                        Page: pageWindow,
                        NextToken: cursor,
                    Parameters: parameters)
                    {
                        OnDatasetFacts = onFacts,
                    },
                    userPrincipal,
                    tenantId,
                    ct).ConfigureAwait(false);
            }
            catch (SqlPagingUnsupportedException) when (cursor is null && requestedLimit is null)
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    new GovernedSqlQueryRequest(query, RowLimit: boundedRowLimit, Transport: SqlTransports.Mcp, Parameters: parameters)
                    {
                        OnDatasetFacts = onFacts,
                    },
                    userPrincipal,
                    tenantId,
                    ct).ConfigureAwait(false);
            }

            McpResultMasking.Report(sqlResult.HasPolicyMaskedValues);
            McpRowContinuation.Offer(sqlResult.ResumeAfter);
            IReadOnlyDictionary<string, object?>? factsResponse = null;
            if (datasetFacts)
            {
                var tables = await Autheris.Application.Sql.Tree.SqlDatasetFacts.ResolveAsync(
                    _catalogVisibility,
                    new Autheris.Application.Catalog.Interfaces.CatalogCaller(userPrincipal, tenantId, Autheris.Application.Catalog.Interfaces.CatalogChannel.Mcp),
                    accessFacts,
                    ct).ConfigureAwait(false);
                factsResponse = Autheris.Application.Sql.Tree.SqlDatasetFacts.ToResponse(tables, sqlResult.Truncated);
            }

            return JsonSerializer.Serialize(McpSqlQueryResult.From(sqlResult, factsResponse), JsonOpts);
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
