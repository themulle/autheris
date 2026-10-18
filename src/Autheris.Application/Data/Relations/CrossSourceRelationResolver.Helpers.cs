namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public sealed partial class CrossSourceRelationResolver
{
    internal static bool TryExtractKey(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> keyColumns,
        out CompositeKey key)
    {
        if (keyColumns.Count == 0)
        {
            key = default;
            return false;
        }

        var values = new object?[keyColumns.Count];
        for (int i = 0; i < keyColumns.Count; i++)
        {
            if (!TryGetColumnValue(row, keyColumns[i], out var val) || val is null or DBNull)
            {
                key = default;
                return false;
            }

            values[i] = val;
        }

        key = new CompositeKey(values);
        return true;
    }

    private static bool TryGetColumnValue(IReadOnlyDictionary<string, object?> row, string columnName, out object? value)
    {
        if (row.TryGetValue(columnName, out value))
        {
            return true;
        }

        foreach (var (k, v) in row)
        {
            if (string.Equals(k, columnName, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = null;
        return false;
    }

    private async Task<TableMetadata> RequireTableMetadataAsync(TableIdentifier table, CancellationToken ct)
    {
        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false)
            ?? throw new TableNotFoundException(table);

        if (!metadata.Table.IsActive)
        {
            throw new GatewayForbiddenException($"Table '{table}' is not active.");
        }

        return metadata;
    }

    private async Task<TableAccessDecision> EvaluateTableDecisionAsync(
        TableIdentifier table,
        TableMetadata metadata,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? clientIp,
        IReadOnlyList<string>? requestedColumns,
        CancellationToken ct)
    {
        GovernedQueryStages.EnsureCanonicalClusterAdminClaim(user, table);
        var ip = !string.IsNullOrWhiteSpace(clientIp) && IPAddress.TryParse(clientIp, out var parsed) ? parsed : null;
        var securityContext = SecurityPrincipalContext.FromPrincipal(user, tenantId, clientIp: ip);
        return await _pdp.EvaluateAccessAsync(table, metadata, securityContext, requestedColumns, ct).ConfigureAwait(false);
    }

    private void ValidateCrossSourceOptIn(TableRelation relation, TableMetadata parentMetadata, TableMetadata childMetadata)
    {
        if (IsCrossDataSource(parentMetadata, childMetadata) && !relation.AllowCrossDataSource)
        {
            throw new CrossSourceRelationException(
                CrossSourceRelationErrorCodes.CrossSourceRelationNotEnabled,
                $"Relation '{relation.RelationName}' between '{parentMetadata.Identifier}' and '{childMetadata.Identifier}' crosses data sources, but AllowCrossDataSource is false.");
        }
    }

    private static void ValidateJoinKeysClear(
        TableRelation relation,
        TableMetadata parentMetadata,
        TableAccessDecision parentDecision,
        TableMetadata childMetadata,
        TableAccessDecision childDecision)
    {
        if (relation.JoinKeysParent.Count == 0 || relation.JoinKeysChild.Count == 0 ||
            relation.JoinKeysParent.Count != relation.JoinKeysChild.Count)
        {
            throw new CrossSourceRelationException(
                CrossSourceRelationErrorCodes.CrossSourceRelationKeyNotClear,
                $"Relation '{relation.RelationName}' must declare matching non-empty parent and child join keys.");
        }

        foreach (var parentKey in relation.JoinKeysParent)
        {
            var col = parentMetadata.GetColumn(parentKey);
            if (col == null || parentDecision.GetEffectiveColumnAccess(col.ColumnName, parentMetadata) != ColumnAccessLevel.Clear)
            {
                throw new CrossSourceRelationException(
                    CrossSourceRelationErrorCodes.CrossSourceRelationKeyNotClear,
                    $"Parent join key column '{parentKey}' on table '{parentMetadata.Identifier}' must have Clear access for cross-source relation resolution.");
            }
        }

        foreach (var childKey in relation.JoinKeysChild)
        {
            var col = childMetadata.GetColumn(childKey);
            if (col == null || childDecision.GetEffectiveColumnAccess(col.ColumnName, childMetadata) != ColumnAccessLevel.Clear)
            {
                throw new CrossSourceRelationException(
                    CrossSourceRelationErrorCodes.CrossSourceRelationKeyNotClear,
                    $"Target join key column '{childKey}' on table '{childMetadata.Identifier}' must have Clear access for cross-source relation resolution.");
            }
        }
    }

    private static void ValidateFilterColumnsClear(
        IReadOnlyList<string> referencedColumns,
        TableMetadata childMetadata,
        TableAccessDecision childDecision)
    {
        foreach (var colName in referencedColumns)
        {
            var col = childMetadata.GetColumn(colName);
            if (col == null)
            {
                throw new GatewayInvalidQueryException($"Filter column '{colName}' does not exist on target table '{childMetadata.Identifier}'.");
            }

            if (childDecision.GetEffectiveColumnAccess(col.ColumnName, childMetadata) != ColumnAccessLevel.Clear)
            {
                throw new FilterOnMaskedColumnException(col.ColumnName);
            }
        }
    }

    private static IReadOnlyList<string> BuildRequestedChildColumns(
        IReadOnlyList<string>? selectColumns,
        IReadOnlyList<string> joinKeysChild,
        TableMetadata childMetadata)
    {
        var baseCols = selectColumns is { Count: > 0 }
            ? selectColumns
            : childMetadata.Columns.Select(c => c.ColumnName).ToList();

        var combined = new List<string>(baseCols);
        foreach (var jk in joinKeysChild)
        {
            if (!combined.Contains(jk, StringComparer.OrdinalIgnoreCase))
            {
                combined.Add(jk);
            }
        }

        return combined;
    }

    private static IReadOnlyList<string> ResolveProjectedColumns(
        IReadOnlyList<string>? selectColumns,
        TableMetadata childMetadata,
        TableAccessDecision childDecision)
    {
        if (selectColumns is { Count: > 0 })
        {
            var result = new List<string>(selectColumns.Count);
            foreach (var requested in selectColumns)
            {
                var col = childMetadata.GetColumn(requested)
                    ?? throw new GatewayInvalidQueryException($"Column '{requested}' does not exist on target table '{childMetadata.Identifier}'.");

                if (childDecision.GetEffectiveColumnAccess(col.ColumnName, childMetadata) == ColumnAccessLevel.Deny)
                {
                    throw new ColumnNotReleasedException(col.ColumnName);
                }

                result.Add(col.ColumnName);
            }

            return result;
        }

        return childMetadata.Columns
            .Where(c => childDecision.GetEffectiveColumnAccess(c.ColumnName, childMetadata) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();
    }

    private int ResolveEffectiveBatchSize(TableMetadata childMetadata, int keyColumnCount)
    {
        int defaultBatch = Math.Max(1, _options.MaxBatchSize);
        if (_parameterBudgetProvider == null)
        {
            return defaultBatch;
        }

        return Math.Max(1, _parameterBudgetProvider.CalculateEffectiveChunkSize(
            defaultChunkSize: defaultBatch,
            keyColumnCount: Math.Max(1, keyColumnCount),
            contextParameterCount: 4,
            dialect: childMetadata.Dialect));
    }

    private static bool PassesTargetGovernance(
        IReadOnlyDictionary<string, object?> row,
        TableMetadata childMetadata,
        TableAccessDecision childDecision,
        TenantId tenantId,
        string? targetFilterSql)
    {
        var tenantCol = childMetadata.TenantColumnName;
        if (tenantCol != null && TryGetColumnValue(row, tenantCol, out var rowTenant) && rowTenant != null)
        {
            if (!string.Equals(rowTenant.ToString(), tenantId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // REQ-GOV-129 (A-8): typed policy first (bound caller attributes), text only for a decision without one.
        if (!InMemoryRowFilterEvaluator.RowPassesDecision(row, childDecision, childMetadata))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetFilterSql) &&
            !StreamingRowFilterAstEvaluator.Matches(row, targetFilterSql))
        {
            return false;
        }

        return true;
    }

    private IReadOnlyDictionary<string, object?> ProjectAndMaskRow(
        IReadOnlyDictionary<string, object?> rawRow,
        IReadOnlyList<string> columnsToProject,
        TableMetadata childMetadata,
        TableAccessDecision childDecision,
        TenantId tenantId)
    {
        var projected = new Dictionary<string, object?>(columnsToProject.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var colName in columnsToProject)
        {
            var access = childDecision.GetEffectiveColumnAccess(colName, childMetadata);
            if (access == ColumnAccessLevel.Deny)
            {
                continue;
            }

            if (!TryGetColumnValue(rawRow, colName, out var val) || val is null or DBNull)
            {
                projected[colName] = null;
                continue;
            }

            if (access == ColumnAccessLevel.Mask)
            {
                var rule = childMetadata.ColumnMaskingRules.TryGetValue(colName, out var r) ? r : DefaultRedactRule;
                var scopedRule = GatewayExecutionService.ScopeRuleForTenant(rule, tenantId.Value, null);
                projected[colName] = _maskingProvider.MaskValue(colName, val, scopedRule);
            }
            else
            {
                projected[colName] = val;
            }
        }

        return projected;
    }

    private static (string PredicateSql, IReadOnlyDictionary<string, object?> Parameters) BuildParentInPredicate(
        IReadOnlyList<string> parentJoinKeys,
        IReadOnlyList<CompositeKey> matchingKeys)
    {
        if (matchingKeys.Count == 0)
        {
            return ("1 = 0", new Dictionary<string, object?>());
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parentJoinKeys.Count == 1)
        {
            var quotedCol = DataApiFilterTranslator.QuoteIdentifier(parentJoinKeys[0]);
            var paramNames = new List<string>(matchingKeys.Count);
            for (int i = 0; i < matchingKeys.Count; i++)
            {
                var pName = $"__cs_fk_{i.ToString(CultureInfo.InvariantCulture)}";
                parameters[pName] = matchingKeys[i].Values[0];
                paramNames.Add($"@{pName}");
            }

            return ($"{quotedCol} IN ({string.Join(", ", paramNames)})", parameters);
        }

        var disjuncts = new List<string>(matchingKeys.Count);
        for (int i = 0; i < matchingKeys.Count; i++)
        {
            var conjuncts = new List<string>(parentJoinKeys.Count);
            for (int k = 0; k < parentJoinKeys.Count; k++)
            {
                var quotedCol = DataApiFilterTranslator.QuoteIdentifier(parentJoinKeys[k]);
                var pName = $"__cs_fk_{i.ToString(CultureInfo.InvariantCulture)}_{k.ToString(CultureInfo.InvariantCulture)}";
                parameters[pName] = matchingKeys[i].Values[k];
                conjuncts.Add($"{quotedCol} = @{pName}");
            }

            disjuncts.Add($"({string.Join(" AND ", conjuncts)})");
        }

        return ($"({string.Join(" OR ", disjuncts)})", parameters);
    }

    private Task RecordTargetAuditAsync(
        TenantId tenantId,
        ClaimsPrincipal user,
        TableRelation relation,
        TableIdentifier targetTable,
        bool allowed,
        int batchesExecuted,
        int rowsMaterialized,
        CancellationToken ct)
    {
        var actorSid = user.GetUserSid() ?? new Sid(user.Identity?.Name ?? "anonymous");
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["relation"] = relation.RelationName,
            ["parent_table"] = relation.ParentTableIdentifier.ToString(),
            ["target_table"] = targetTable.ToString(),
            ["batches_executed"] = batchesExecuted,
            ["rows_materialized"] = rowsMaterialized,
            ["path"] = "cross_source_relation"
        };

        return GovernedQueryStages.RecordTableReadAsync(
            _auditLogRepository,
            tenantId,
            actorSid,
            targetTable,
            allowed,
            details,
            ct,
            eventType: AuditEventTypes.WebSqlCrossSourceSourceRead);
    }
}
