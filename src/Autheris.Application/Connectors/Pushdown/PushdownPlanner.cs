using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Connectors.Pushdown;

public sealed class PushdownPlanner : IPushdownPlanner
{
    public PushdownExecutionPlan CreatePlan(
        TableMetadata metadata,
        ConnectorCapabilities capabilities,
        TableAccessDecision decision,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, object?>? queryArguments,
        int? first,
        int? after,
        int maxGatewayRows = 1000)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(decision);

        // 1. Column projection pushdown calculation
        var authorizedColumns = metadata.Columns
            .Where(c => decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();

        IReadOnlyList<string> effectiveProjected;
        if (capabilities.HasFeature(ConnectorFeatures.ProjectionPushdown))
        {
            effectiveProjected = (requestedFields != null && requestedFields.Count > 0)
                ? requestedFields.Where(f => authorizedColumns.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList()
                : authorizedColumns;

            if (effectiveProjected.Count == 0 && authorizedColumns.Count > 0)
            {
                effectiveProjected = authorizedColumns;
            }
        }
        else
        {
            // If connector cannot project, request all authorized columns and filter in memory
            effectiveProjected = authorizedColumns;
        }

        // 2. Filter pushdown vs residual memory filter
        string? pushedFilterSql = null;
        string? residualFilterSql = null;
        bool requiresMemoryFilter = false;

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            if (capabilities.HasFeature(ConnectorFeatures.FilterPushdown))
            {
                pushedFilterSql = decision.CombinedRowFilterSql;
                requiresMemoryFilter = false;
            }
            else
            {
                residualFilterSql = decision.CombinedRowFilterSql;
                requiresMemoryFilter = true;
            }
        }

        // 3. Limit and offset pushdown calculation
        int ceiling = Math.Min(capabilities.MaxBatchSize, maxGatewayRows > 0 ? maxGatewayRows : 1000);
        int effectiveLimit = Math.Clamp(first ?? 50, 1, ceiling);
        int effectiveOffset = Math.Max(0, after ?? 0);

        // 4. Query arguments
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["limit"] = effectiveLimit,
            ["offset"] = effectiveOffset
        };

        if (queryArguments != null)
        {
            foreach (var (k, v) in queryArguments)
            {
                args[k] = v;
            }
        }

        // 5. Masking requirements
        bool hasMaskedColumns = authorizedColumns.Any(col => decision.GetColumnAccess(col) == ColumnAccessLevel.Mask);

        return new PushdownExecutionPlan(
            Table: metadata.Identifier,
            ProjectedColumns: effectiveProjected,
            PushedFilterSql: pushedFilterSql,
            PushedArguments: args,
            Limit: effectiveLimit,
            Offset: effectiveOffset,
            RequiresMemoryFilter: requiresMemoryFilter,
            RequiresMemoryMasking: hasMaskedColumns,
            ResidualFilterSql: residualFilterSql);
    }
}

public interface IPushdownPlanner
{
    PushdownExecutionPlan CreatePlan(
        TableMetadata metadata,
        ConnectorCapabilities capabilities,
        TableAccessDecision decision,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, object?>? queryArguments,
        int? first,
        int? after,
        int maxGatewayRows = 1000);
}
