namespace Autheris.Application.Sql.Services;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

public sealed partial class FederatedDuckDbExecutionService
{
    private static System.Collections.Immutable.ImmutableArray<bool> MaskedColumnFlags(Autheris.Application.Olap.OlapQueryResult result, CrossSourcePlan plan) =>
        System.Collections.Immutable.ImmutableArray.CreateRange(result.Columns.Select(c => IsMaskedByName(c, plan)));

    private static bool IsMaskedByName(string columnName, CrossSourcePlan plan)
    {
        bool foundMatch = false;
        foreach (var req in plan.StagingRequests)
        {
            if (req.Subtree != null)
            {
                if (req.Subtree.ProjectedColumns.Any(c => string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase)))
                {
                    foundMatch = true;
                    if (req.Subtree.MaskedColumns.Contains(columnName))
                    {
                        return true;
                    }
                }
            }
            else
            {
                var matchingCol = req.Metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
                if (matchingCol != null)
                {
                    foundMatch = true;
                    if (req.Decision.GetEffectiveColumnAccess(matchingCol.ColumnName, req.Metadata) == ColumnAccessLevel.Mask)
                    {
                        return true;
                    }
                }
            }
        }

        return !foundMatch;
    }

    public async Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(user);

        var crossSourceOptions = _options.Value.WebSql?.CrossSource ?? new CrossSourceOptions();
        if (!crossSourceOptions.Enabled)
        {
            throw new WebSqlPolicyException("Cross-catalog queries across multiple data sources are not supported in WebSQL.");
        }

        var sqlForAnalysis = GovernedSqlExecutionService.NormalizeClientParameters(rawSql, null, out _);
        var metadata = await _sqlEngine.AnalyzeAsync(sqlForAnalysis.AsMemory(), AnalysisTokenOptions, ct).ConfigureAwait(false);

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            throw new WebSqlPolicyException("DML statements across multiple data sources or HTTP APIs are not permitted in WebSQL.");
        }

        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var allowedFunctions = EffectiveDuckDbFunctions();
            foreach (var functionName in metadata.FunctionCalls)
            {
                if (!SqlFunctionPolicy.IsFunctionAllowed(functionName, allowedFunctions, additionalDeniedFunctions: null))
                {
                    throw new WebSqlPolicyException($"Function '{functionName}' is not permitted in WebSQL.");
                }
            }
        }

        var routing = await _router.RouteAsync(metadata, requestedDataSource: null, tenantId, isDml: false, ct).ConfigureAwait(false);
        if (routing.SourceClass == QuerySourceClass.Unsupported)
        {
            throw new WebSqlPolicyException(routing.RejectionReason ?? "Unsupported data source type.");
        }

        var (decisions, _) = await AuthorizeAllTablesAsync(metadata, routing.Tables, user, tenantId, isDml: false, ct).ConfigureAwait(false);

        // Every routed source must allow the ingress transport; nothing is planned or staged otherwise.
        await _transportGate.EnsureCatalogSourcesAsync(routing.Tables.Select(t => t.EffectiveDataSource), ct).ConfigureAwait(false);

        _planner.Plan(sqlForAnalysis, metadata, routing.Tables, decisions, crossSourceOptions, user, tenantId);
    }
}
