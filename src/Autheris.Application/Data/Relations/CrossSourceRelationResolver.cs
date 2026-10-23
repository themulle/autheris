namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #75: Resolves explicit cross-data-source <see cref="TableRelation"/> navigations via gateway-level
/// batched key lookup (<c>DataLoader</c> pattern).
/// </summary>
/// <remarks>Not used by the GraphQL path; replaced by WP-FED-06.</remarks>
public sealed partial class CrossSourceRelationResolver : ICrossSourceRelationResolver
{
    private static readonly MaskingRule DefaultRedactRule = new() { RuleType = "REDACT", Replacement = "[REDACTED]" };

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly ICrossSourceRowReader _rowReader;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IParameterBudgetProvider? _parameterBudgetProvider;
    private readonly CrossSourceRelationOptions _options;

    public CrossSourceRelationResolver(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        ICrossSourceRowReader rowReader,
        IColumnMaskingProvider maskingProvider,
        IAuditLogRepository auditLogRepository,
        IParameterBudgetProvider? parameterBudgetProvider = null,
        IOptions<CrossSourceRelationOptions>? options = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _rowReader = rowReader ?? throw new ArgumentNullException(nameof(rowReader));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _parameterBudgetProvider = parameterBudgetProvider;
        _options = options?.Value ?? new CrossSourceRelationOptions();
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

        // VF-02: a child with an active Virtual Filter cannot be enforced on this path (the filter is a subquery that the in-memory evaluation
        // cannot run); the request is refused instead of returning wrong rows.
        if (childDecision.MandatoryRowPredicateSql != null)
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
            Autheris.Application.Services.InMemoryRowFilterEvaluator.EnsureInMemoryFilterIsEnforceable(childDecision);
        }

        ValidateJoinKeysClear(relation, parentMetadata, parentDecision, childMetadata, childDecision);

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
        var readColumns = ResolveReadColumns(columnsToProject, relation.JoinKeysChild, childMetadata, childDecision);
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
                new CrossSourceKeyReadRequest(
                    childMetadata,
                    relation.JoinKeysChild,
                    readColumns,
                    batch,
                    request.TenantId,
                    UserSidOf(request.User)),
                ct).ConfigureAwait(false);

            foreach (var rawRow in rawRows)
            {
                if (!PassesTargetGovernance(rawRow, childMetadata, childDecision, request.TenantId))
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
                    ? matchedRows.GetRange(0, request.PerParentLimit.Value)
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
}
