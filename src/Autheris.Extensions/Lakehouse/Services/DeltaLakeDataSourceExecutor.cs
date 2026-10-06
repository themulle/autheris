namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// P11: Modern Lakehouse Connector - Governed query executor for Delta Lake (UniForm) tables.
/// </summary>
public sealed class DeltaLakeDataSourceExecutor : IDataSourceExecutor
{
    public DataSourceType SupportedType => DataSourceType.LakehouseDelta;

    private const string TenantColumn = "tenantId";

    private readonly IDeltaMetadataReader _metadataReader;
    private readonly IDeltaPartitionPruner _partitionPruner;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DeltaLakeDataSourceExecutor> _logger;

    public DeltaLakeDataSourceExecutor(
        IDeltaMetadataReader metadataReader,
        IDeltaPartitionPruner partitionPruner,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ILogger<DeltaLakeDataSourceExecutor> logger)
    {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _partitionPruner = partitionPruner ?? throw new ArgumentNullException(nameof(partitionPruner));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // SEC M-35: Enforce access decision & tenant isolation (fail-closed)
        if (!context.AccessDecision.IsAllowed && !_options.Value.IsLakehouseAuthBypassed)
        {
            _logger.LogWarning("Access to Delta Lake table '{Table}' denied for user '{User}'.",
                context.Metadata.Identifier, context.Principal?.Identity?.Name);
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var tenantId = context.Tenant?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) && !_options.Value.IsLakehouseAuthBypassed)
        {
            _logger.LogWarning("Delta Lake scan of '{Table}' rejected: no tenant context available.", context.Metadata.Identifier);
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var predicates = ExtractPredicates(context.Arguments);

        // SEC-DL-02 / SEC M-35 / EX-17: Reject filter on uncataloged columns or non-clear columns to prevent inference side-channels
        foreach (var column in predicates.Keys.ToList())
        {
            var colMeta = context.Metadata.GetColumn(column);
            if (colMeta == null)
            {
                throw new Autheris.Domain.Exceptions.GatewaySecurityException(
                    $"Delta Lake scan of '{context.Metadata.Identifier}' rejected: Filter on uncataloged column '{column}' is not permitted.");
            }

            if (context.AccessDecision.GetEffectiveColumnAccess(column, context.Metadata) != ColumnAccessLevel.Clear)
            {
                _logger.LogWarning("Ignoring Delta Lake filter on non-clear column '{Column}' of '{Table}'.", column, context.Metadata.Identifier);
                predicates.Remove(column);
            }
        }

        // SEC E-15: tenant isolation is a mandatory predicate (not only a file-level hint) and is re-checked per row below.
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            if (predicates.TryGetValue(TenantColumn, out var callerTenant) &&
                !string.Equals(callerTenant.Trim(), tenantId, StringComparison.Ordinal))
            {
                throw new Autheris.Domain.Exceptions.GatewaySecurityException(
                    $"Tenant isolation violation: Supplied tenant predicate '{callerTenant}' does not match session tenant.");
            }

            predicates[TenantColumn] = tenantId;
        }

        var tableLocation = context.Metadata.Table.TableName;
        var snapshot = await _metadataReader.LoadSnapshotAsync(tableLocation, null, null, ct).ConfigureAwait(false);

        // Partition & bound pruning
        var prunedFiles = _partitionPruner.PruneDataFiles(snapshot.ActiveFiles, snapshot.Metadata.PartitionColumns, predicates);

        // Generate synthetic row data for pruned files respecting schema & partition values
        var rawRows = new List<Dictionary<string, object?>>();
        var selectedCols = context.RequestedFields != null && context.RequestedFields.Count > 0
            ? context.RequestedFields
            : snapshot.Metadata.Schema.Fields.Select(f => f.Name).ToList();

        foreach (var file in prunedFiles)
        {
            // SEC E-15: row-level tenant check - a file whose stored tenant differs from the session tenant contributes no rows.
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                file.PartitionValues.TryGetValue(TenantColumn, out var storedTenant) &&
                !string.Equals(storedTenant, tenantId, StringComparison.Ordinal))
            {
                continue;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in selectedCols)
            {
                if (file.PartitionValues.TryGetValue(col, out var pv))
                {
                    row[col] = pv;
                }
                else if (string.Equals(col, "tenantId", StringComparison.OrdinalIgnoreCase))
                {
                    row[col] = tenantId;
                }
                else
                {
                    row[col] = $"val_{col}_{file.Path.GetHashCode():X8}";
                }
            }
            rawRows.Add(row);

            if (context.Limit > 0 && rawRows.Count >= context.Limit)
            {
                break;
            }
        }

        // SEC EX-04: Evaluate RLS before column masking
        var candidateRows = rawRows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            candidateRows = Autheris.Application.Services.GatewayExecutionService.FilterRows(
                candidateRows,
                context.AccessDecision.CombinedRowFilterSql,
                context.Metadata);
            context.Items["RlsPushdownExecuted"] = true;
        }

        // Column Masking
        bool maskingDisabled = _options.Value.IsColumnMaskingDisabled;
        var resultRows = new List<IReadOnlyDictionary<string, object?>>(candidateRows.Count);

        foreach (var row in candidateRows)
        {
            var cleanRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in row)
            {
                var access = context.AccessDecision.GetEffectiveColumnAccess(kvp.Key, context.Metadata);
                if (access == ColumnAccessLevel.Deny)
                {
                    continue;
                }

                var val = kvp.Value;
                if (access == ColumnAccessLevel.Mask && val != null && !maskingDisabled)
                {
                    var rule = context.Metadata.ColumnMaskingRules.TryGetValue(kvp.Key, out var mRule)
                        ? mRule
                        : new MaskingRule { RuleType = "REDACT" };
                    val = _maskingProvider.MaskValue(kvp.Key, val, rule);
                }

                cleanRow[kvp.Key] = val;
            }
            resultRows.Add(cleanRow);
        }

        return resultRows;
    }

    private static Dictionary<string, string> ExtractPredicates(IReadOnlyDictionary<string, object?> arguments)
    {
        var predicates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in arguments)
        {
            if (string.Equals(kvp.Key, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kvp.Value != null)
            {
                predicates[kvp.Key] = kvp.Value.ToString() ?? "";
            }
        }
        return predicates;
    }
}
