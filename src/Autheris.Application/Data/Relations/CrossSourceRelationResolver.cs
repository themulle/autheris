namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// In-memory implementation of <see cref="ICrossSourceRowReader"/> for unit testing and federated staging (#75).
/// </summary>
public sealed class InMemoryCrossSourceRowReader : ICrossSourceRowReader
{
    private readonly ConcurrentDictionary<TableIdentifier, List<IReadOnlyDictionary<string, object?>>> _tables = new();

    public void SeedTable(TableIdentifier table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        _tables[table] = rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        TableMetadata targetTable,
        IReadOnlyList<string> joinKeyColumns,
        IReadOnlyList<CompositeKey> batchKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);
        ArgumentNullException.ThrowIfNull(joinKeyColumns);
        ArgumentNullException.ThrowIfNull(batchKeys);

        if (!_tables.TryGetValue(targetTable.Identifier, out var rows) || rows.Count == 0 || batchKeys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var keySet = new HashSet<CompositeKey>(batchKeys);
        var matched = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            if (CrossSourceRelationResolver.TryExtractKey(row, joinKeyColumns, out var key) && keySet.Contains(key))
            {
                matched.Add(row);
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsForFilterAsync(
        TableMetadata targetTable,
        string filterSql,
        int maxRows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);

        if (!_tables.TryGetValue(targetTable.Identifier, out var rows) || rows.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var matched = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(filterSql) || StreamingRowFilterAstEvaluator.Matches(row, filterSql))
            {
                matched.Add(row);
                if (maxRows > 0 && matched.Count >= maxRows)
                {
                    break;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }
}

/// <summary>
/// Issue #75: Resolves explicit cross-data-source <see cref="TableRelation"/> navigations via gateway-level
/// batched key lookup (<c>DataLoader</c> pattern) and bounded cross-source filter key pushdown.
/// </summary>
public sealed class CrossSourceRelationResolver : ICrossSourceRelationResolver
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly ICrossSourceRowReader _rowReader;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IParameterBudgetProvider? _parameterBudgetProvider;
    private readonly CrossSourceRelationOptions _options;
    private readonly GatewayOptions _gatewayOptions;

    public CrossSourceRelationResolver(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        ICrossSourceRowReader rowReader,
        IColumnMaskingProvider maskingProvider,
        IAuditLogRepository auditLogRepository,
        IParameterBudgetProvider? parameterBudgetProvider = null,
        IOptions<CrossSourceRelationOptions>? options = null,
        IOptions<GatewayOptions>? gatewayOptions = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _rowReader = rowReader ?? throw new ArgumentNullException(nameof(rowReader));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _parameterBudgetProvider = parameterBudgetProvider;
        _options = options?.Value ?? new CrossSourceRelationOptions();
        _gatewayOptions = gatewayOptions?.Value ?? new GatewayOptions();
    }

    public bool IsCrossDataSource(TableMetadata parentTable, TableMetadata childTable)
    {
        ArgumentNullException.ThrowIfNull(parentTable);
        ArgumentNullException.ThrowIfNull(childTable);

        if (parentTable.DataSourceType != childTable.DataSourceType)
        {
            return true;
        }

        if (!string.Equals(parentTable.Table.SourceName, childTable.Table.SourceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(parentTable.Identifier.Domain, childTable.Identifier.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public void ValidateNoCrossSourceSort(
        TableRelation relation,
        TableMetadata parentTable,
        TableMetadata childTable,
        IReadOnlyList<string>? sortByTargetColumns)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(parentTable);
        ArgumentNullException.ThrowIfNull(childTable);

        if (sortByTargetColumns is { Count: > 0 } && IsCrossDataSource(parentTable, childTable))
        {
            throw new CrossSourceRelationException(
                CrossSourceRelationErrorCodes.CrossSourceSortNotSupported,
                $"Sorting parent table '{parentTable.Identifier}' by cross-source relation '{relation.RelationName}' on '{childTable.Identifier}' is not supported.");
        }
    }

    public async Task<CrossSourceRelationResult> ResolveRelationAsync(
        CrossSourceRelationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Relation);
        ArgumentNullException.ThrowIfNull(request.ParentRows);
        ArgumentNullException.ThrowIfNull(request.User);

        var relation = request.Relation;
        var parentMetadata = await RequireTableMetadataAsync(relation.ParentTableIdentifier, ct).ConfigureAwait(false);
        var childMetadata = await RequireTableMetadataAsync(relation.ChildTableIdentifier, ct).ConfigureAwait(false);

        ValidateCrossSourceOptIn(relation, parentMetadata, childMetadata);
        ValidateNoCrossSourceSort(relation, parentMetadata, childMetadata, request.SortByTargetColumns);

        var parentDecision = request.ParentDecision ?? await EvaluateTableDecisionAsync(
            relation.ParentTableIdentifier,
            parentMetadata,
            request.User,
            request.TenantId,
            request.ClientIp,
            relation.JoinKeysParent,
            ct).ConfigureAwait(false);

        if (!parentDecision.IsAllowed)
        {
            throw new GatewayForbiddenException($"Access to parent table '{relation.ParentTableIdentifier}' is denied.");
        }

        var requestedChildCols = BuildRequestedChildColumns(request.SelectColumns, relation.JoinKeysChild, childMetadata);
        var childDecision = await EvaluateTableDecisionAsync(
            relation.ChildTableIdentifier,
            childMetadata,
            request.User,
            request.TenantId,
            request.ClientIp,
            requestedChildCols,
            ct).ConfigureAwait(false);

        if (!childDecision.IsAllowed)
        {
            await RecordTargetAuditAsync(
                request.TenantId,
                request.User,
                relation,
                childMetadata.Identifier,
                allowed: false,
                batchesExecuted: 0,
                rowsMaterialized: 0,
                ct).ConfigureAwait(false);
            throw new GatewayForbiddenException($"Access to target table '{relation.ChildTableIdentifier}' is denied.");
        }

        ValidateJoinKeysClear(relation, parentMetadata, parentDecision, childMetadata, childDecision);

        if (!string.IsNullOrWhiteSpace(request.TargetFilterSql))
        {
            var referenced = DataApiFilterTranslator.ExtractReferencedColumns(request.TargetFilterSql, childMetadata);
            ValidateFilterColumnsClear(referenced, childMetadata, childDecision);
        }

        // Extract distinct non-null join keys from the parent page
        var parentKeysByIndex = new CompositeKey?[request.ParentRows.Count];
        var distinctKeys = new List<CompositeKey>();
        var seenKeys = new HashSet<CompositeKey>();

        for (int i = 0; i < request.ParentRows.Count; i++)
        {
            if (TryExtractKey(request.ParentRows[i], relation.JoinKeysParent, out var key))
            {
                parentKeysByIndex[i] = key;
                if (seenKeys.Add(key))
                {
                    distinctKeys.Add(key);
                }
            }
        }

        var columnsToProject = ResolveProjectedColumns(request.SelectColumns, childMetadata, childDecision);
        int batchSize = ResolveEffectiveBatchSize(childMetadata, relation.JoinKeysChild.Count);

        var groupedByTargetKey = new Dictionary<CompositeKey, List<IReadOnlyDictionary<string, object?>>>();
        int batchesExecuted = 0;
        int totalMaterialized = 0;

        for (int offset = 0; offset < distinctKeys.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, distinctKeys.Count - offset);
            var batch = distinctKeys.GetRange(offset, count);
            batchesExecuted++;

            var rawRows = await _rowReader.ReadRowsByKeysAsync(
                childMetadata,
                relation.JoinKeysChild,
                batch,
                ct).ConfigureAwait(false);

            foreach (var rawRow in rawRows)
            {
                if (!PassesTargetGovernance(rawRow, childMetadata, childDecision, request.TenantId, request.TargetFilterSql))
                {
                    continue;
                }

                if (!TryExtractKey(rawRow, relation.JoinKeysChild, out var targetKey))
                {
                    continue;
                }

                totalMaterialized++;
                if (totalMaterialized > _options.MaxNestedRows)
                {
                    throw new RowLimitExceededException(
                        $"Cross-source relation '{relation.RelationName}' exceeded the maximum nested row budget of {_options.MaxNestedRows}.");
                }

                var maskedRow = ProjectAndMaskRow(rawRow, columnsToProject, childMetadata, childDecision, request.TenantId);
                if (!groupedByTargetKey.TryGetValue(targetKey, out var list))
                {
                    list = new List<IReadOnlyDictionary<string, object?>>();
                    groupedByTargetKey[targetKey] = list;
                }

                list.Add(maskedRow);
            }
        }

        var manyToOneResults = new IReadOnlyDictionary<string, object?>?[request.ParentRows.Count];
        var oneToManyResults = new IReadOnlyList<IReadOnlyDictionary<string, object?>>[request.ParentRows.Count];

        for (int i = 0; i < request.ParentRows.Count; i++)
        {
            var parentKey = parentKeysByIndex[i];
            if (parentKey.HasValue && groupedByTargetKey.TryGetValue(parentKey.Value, out var matchedRows) && matchedRows.Count > 0)
            {
                manyToOneResults[i] = matchedRows[0];
                oneToManyResults[i] = request.PerParentLimit is > 0 && matchedRows.Count > request.PerParentLimit.Value
                    ? matchedRows.Take(request.PerParentLimit.Value).ToList()
                    : matchedRows;
            }
            else
            {
                manyToOneResults[i] = null;
                oneToManyResults[i] = Array.Empty<IReadOnlyDictionary<string, object?>>();
            }
        }

        double queryCost = (batchesExecuted * _options.CostPerBatch) + (totalMaterialized * _options.CostPerRow);

        await RecordTargetAuditAsync(
            request.TenantId,
            request.User,
            relation,
            childMetadata.Identifier,
            allowed: true,
            batchesExecuted,
            totalMaterialized,
            ct).ConfigureAwait(false);

        return new CrossSourceRelationResult(
            Relation: relation,
            ManyToOneByParentIndex: manyToOneResults,
            OneToManyByParentIndex: oneToManyResults,
            BatchesExecuted: batchesExecuted,
            TargetRowsMaterialized: totalMaterialized,
            QueryCost: queryCost);
    }

    public async Task<CrossSourceFilterKeySetResult> ResolveCrossSourceFilterKeysAsync(
        CrossSourceFilterPushdownRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetFilterSql);
        ArgumentNullException.ThrowIfNull(request.User);

        var relation = request.Relation;
        var parentMetadata = await RequireTableMetadataAsync(relation.ParentTableIdentifier, ct).ConfigureAwait(false);
        var childMetadata = await RequireTableMetadataAsync(relation.ChildTableIdentifier, ct).ConfigureAwait(false);

        ValidateCrossSourceOptIn(relation, parentMetadata, childMetadata);

        var parentDecision = request.ParentDecision ?? await EvaluateTableDecisionAsync(
            relation.ParentTableIdentifier,
            parentMetadata,
            request.User,
            request.TenantId,
            request.ClientIp,
            relation.JoinKeysParent,
            ct).ConfigureAwait(false);

        if (!parentDecision.IsAllowed)
        {
            throw new GatewayForbiddenException($"Access to parent table '{relation.ParentTableIdentifier}' is denied.");
        }

        var childDecision = await EvaluateTableDecisionAsync(
            relation.ChildTableIdentifier,
            childMetadata,
            request.User,
            request.TenantId,
            request.ClientIp,
            relation.JoinKeysChild,
            ct).ConfigureAwait(false);

        if (!childDecision.IsAllowed)
        {
            await RecordTargetAuditAsync(
                request.TenantId,
                request.User,
                relation,
                childMetadata.Identifier,
                allowed: false,
                batchesExecuted: 0,
                rowsMaterialized: 0,
                ct).ConfigureAwait(false);
            throw new GatewayForbiddenException($"Access to target table '{relation.ChildTableIdentifier}' is denied.");
        }

        ValidateJoinKeysClear(relation, parentMetadata, parentDecision, childMetadata, childDecision);

        var referencedCols = request.ReferencedTargetColumns is { Count: > 0 }
            ? request.ReferencedTargetColumns
            : DataApiFilterTranslator.ExtractReferencedColumns(request.TargetFilterSql, childMetadata);
        ValidateFilterColumnsClear(referencedCols, childMetadata, childDecision);

        int maxKeys = Math.Max(1, _options.MaxCrossSourceFilterKeys);
        var candidateRows = await _rowReader.ReadRowsForFilterAsync(
            childMetadata,
            request.TargetFilterSql,
            maxRows: 0,
            ct).ConfigureAwait(false);

        var distinctKeys = new List<CompositeKey>();
        var seenKeys = new HashSet<CompositeKey>();

        foreach (var rawRow in candidateRows)
        {
            if (!PassesTargetGovernance(rawRow, childMetadata, childDecision, request.TenantId, request.TargetFilterSql))
            {
                continue;
            }

            if (!TryExtractKey(rawRow, relation.JoinKeysChild, out var targetKey))
            {
                continue;
            }

            if (seenKeys.Add(targetKey))
            {
                distinctKeys.Add(targetKey);
                if (distinctKeys.Count > maxKeys)
                {
                    throw new CrossSourceRelationException(
                        CrossSourceRelationErrorCodes.CrossSourceFilterKeySetExceeded,
                        $"Cross-source filter on relation '{relation.RelationName}' matched more than the maximum allowed {maxKeys} distinct join keys.");
                }
            }
        }

        await RecordTargetAuditAsync(
            request.TenantId,
            request.User,
            relation,
            childMetadata.Identifier,
            allowed: true,
            batchesExecuted: 1,
            rowsMaterialized: distinctKeys.Count,
            ct).ConfigureAwait(false);

        var (predicateSql, parameters) = BuildParentInPredicate(relation.JoinKeysParent, distinctKeys);
        double queryCost = _options.CostPerBatch + (distinctKeys.Count * _options.CostPerRow);

        return new CrossSourceFilterKeySetResult(
            MatchingKeys: distinctKeys,
            ParentWherePredicateSql: predicateSql,
            Parameters: parameters,
            QueryCost: queryCost);
    }

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

        if (!string.IsNullOrWhiteSpace(childDecision.CombinedRowFilterSql) &&
            !StreamingRowFilterAstEvaluator.Matches(row, childDecision.CombinedRowFilterSql))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(childDecision.MandatoryRowPredicateSql) &&
            !StreamingRowFilterAstEvaluator.Matches(row, childDecision.MandatoryRowPredicateSql))
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
        bool maskingDisabled = _gatewayOptions.IsColumnMaskingDisabled;

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

            if (access == ColumnAccessLevel.Mask && !maskingDisabled)
            {
                var rule = childMetadata.ColumnMaskingRules.TryGetValue(colName, out var r) ? r : DefaultRedactRule;
                var scopedRule = GatewayExecutionService.ScopeRuleForTenant(rule, tenantId.Value, _gatewayOptions.DataMasking?.HmacKeyId);
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
