namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
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
/// High-throughput query executor scanning pruned Lakehouse data files with zero-trust governance.
/// </summary>
public sealed class LakehouseDataSourceExecutor : ILakehouseDataSourceExecutor, IDataSourceExecutor
{
    public DataSourceType SupportedType => DataSourceType.LakehouseIceberg;

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.AccessDecision.IsAllowed && !_options.Value.IsLakehouseAuthBypassed)
        {
            _logger.LogWarning("Access to Lakehouse table '{Table}' denied for user '{User}'.", context.Metadata.Identifier, context.Principal?.Identity?.Name);
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var predicates = ExtractPredicates(context.Arguments);

        // SEC M-35 / EX-17: predicates on uncataloged columns or masked/denied columns would turn partition pruning into an inference oracle.
        foreach (var column in predicates.Keys.ToList())
        {
            var colMeta = context.Metadata.GetColumn(column);
            if (colMeta == null)
            {
                throw new Autheris.Domain.Exceptions.GatewaySecurityException(
                    $"Lakehouse scan of '{context.Metadata.Identifier}' rejected: Filter on uncataloged column '{column}' is not permitted.");
            }

            if (context.AccessDecision.GetEffectiveColumnAccess(column, context.Metadata) != ColumnAccessLevel.Clear)
            {
                _logger.LogWarning("Ignoring Lakehouse filter on non-clear column '{Column}' of '{Table}'.", column, context.Metadata.Identifier);
                predicates.Remove(column);
            }
        }

        // SEC M-35: tenant context is mandatory for governed scans (fail-closed).
        var tenantId = context.Tenant?.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) && !_options.Value.IsLakehouseAuthBypassed)
        {
            _logger.LogWarning("Lakehouse scan of '{Table}' rejected: no tenant context available.", context.Metadata.Identifier);
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        var scanReq = new LakehouseScanRequest(
            context.Metadata.Table.TableName,
            context.RequestedFields ?? Array.Empty<string>(),
            predicates,
            tenantId,
            context.Limit);

        var result = await ScanCoreAsync(scanReq, ct).ConfigureAwait(false);

        var rawRows = result.Rows.ToList();

        // SEC EX-04: Evaluate RLS row-filter BEFORE column masking so that comparison predicates
        // (such as "region <> 'US'") evaluate against raw, unmasked values instead of "REDACTED" / "0".
        if (!string.IsNullOrWhiteSpace(context.AccessDecision.CombinedRowFilterSql))
        {
            rawRows = Autheris.Application.Services.GatewayExecutionService.FilterRows(
                rawRows,
                context.AccessDecision.CombinedRowFilterSql,
                context.Metadata);
            context.Items["RlsPushdownExecuted"] = true;
        }

        bool maskingDisabled = _options.Value.IsColumnMaskingDisabled;
        var filteredRows = new List<IReadOnlyDictionary<string, object?>>(rawRows.Count);
        foreach (var row in rawRows)
        {
            var cleanRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in row)
            {
                // SEC M-35: rule-based masking (access decision + catalog sensitivity / masking rules), applies to partition columns too.
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
                    if (IsHmacRule(rule) && !string.IsNullOrWhiteSpace(tenantId))
                    {
                        rule = MaskingRule.CreateTenantScopedHmacRule(rule, tenantId, _options.Value.DataMasking?.HmacKeyId);
                    }
                    val = _maskingProvider.MaskValue(kvp.Key, val, rule);
                }

                cleanRow[kvp.Key] = val;
            }
            filteredRows.Add(cleanRow);
        }

        context.Items["InDbColumnMaskingExecuted"] = true;
        return filteredRows;
    }

    private static Dictionary<string, string> ExtractPredicates(IReadOnlyDictionary<string, object?>? arguments)
    {
        var predicates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments == null) return predicates;

        if (arguments.TryGetValue("where", out var whereObj) && whereObj is IReadOnlyDictionary<string, object?> whereDict)
        {
            foreach (var kvp in whereDict)
            {
                if (kvp.Value is IReadOnlyDictionary<string, object?> opDict)
                {
                    if (opDict.TryGetValue("eq", out var eqVal) && eqVal != null)
                        predicates[kvp.Key] = $"== {eqVal}";
                    else if (opDict.TryGetValue("neq", out var neqVal) && neqVal != null)
                        predicates[kvp.Key] = $"!= {neqVal}";
                    else if (opDict.TryGetValue("gt", out var gtVal) && gtVal != null)
                        predicates[kvp.Key] = $"> {gtVal}";
                    else if (opDict.TryGetValue("gte", out var gteVal) && gteVal != null)
                        predicates[kvp.Key] = $">= {gteVal}";
                    else if (opDict.TryGetValue("lt", out var ltVal) && ltVal != null)
                        predicates[kvp.Key] = $"< {ltVal}";
                    else if (opDict.TryGetValue("lte", out var lteVal) && lteVal != null)
                        predicates[kvp.Key] = $"<= {lteVal}";
                }
                else if (kvp.Value != null)
                {
                    predicates[kvp.Key] = $"== {kvp.Value}";
                }
            }
        }

        foreach (var kvp in arguments)
        {
            if (string.Equals(kvp.Key, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "limit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (kvp.Value != null && !predicates.ContainsKey(kvp.Key))
            {
                predicates[kvp.Key] = $"== {kvp.Value}";
            }
        }

        return predicates;
    }
    private const string DefaultTenantColumn = "tenantId";

    private readonly IIcebergMetadataReader _metadataReader;
    private readonly IIcebergPartitionPruner _partitionPruner;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IDemoDataSwitch? _demoData;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<LakehouseDataSourceExecutor> _logger;

    public LakehouseDataSourceExecutor(
        IIcebergMetadataReader metadataReader,
        IIcebergPartitionPruner partitionPruner,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions> options,
        ILogger<LakehouseDataSourceExecutor> logger,
        IDemoDataSwitch? demoData = null)
    {
        _demoData = demoData;
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _partitionPruner = partitionPruner ?? throw new ArgumentNullException(nameof(partitionPruner));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ungoverned scan API (no access decision available): every column except the tenant column is masked (fail-closed).
    /// </summary>
    public async ValueTask<LakehouseScanResult> ExecuteScanAsync(
        LakehouseScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = await ScanCoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (_options.Value.IsColumnMaskingDisabled)
        {
            return raw;
        }

        var redact = new MaskingRule { RuleType = "REDACT" };
        var maskedRows = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            var masked = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in row)
            {
                masked[kvp.Key] = kvp.Value != null && !string.Equals(kvp.Key, LakehouseLocationGuard.ResolveTenantColumn(row.Keys, DefaultTenantColumn), StringComparison.OrdinalIgnoreCase)
                    ? _maskingProvider.MaskValue(kvp.Key, kvp.Value, redact)
                    : kvp.Value;
            }
            maskedRows.Add(masked);
        }

        return raw with { Rows = maskedRows };
    }

    private async ValueTask<LakehouseScanResult> ScanCoreAsync(
        LakehouseScanRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // SEC M-35: reject scans without tenant context (fail-closed).
        if (string.IsNullOrWhiteSpace(request.TenantId) && !_options.Value.IsLakehouseAuthBypassed)
        {
            _logger.LogWarning("Lakehouse scan of '{TableName}' rejected: TenantId is required.", request.TableName);
            return new LakehouseScanResult(
                request.TableName,
                Array.Empty<IReadOnlyDictionary<string, object?>>(),
                0, 0, 0.0, stopwatch.Elapsed);
        }

        var lakehouseOpts = _options.Value.Lakehouse;
        if (!lakehouseOpts.Tables.TryGetValue(request.TableName, out var tableConfig))
        {
            _logger.LogWarning("Lakehouse table '{TableName}' is not configured in GatewayOptions.", request.TableName);
            return new LakehouseScanResult(
                request.TableName,
                Array.Empty<IReadOnlyDictionary<string, object?>>(),
                0, 0, 0.0, stopwatch.Elapsed);
        }

        // 1. Load Iceberg Table Metadata & Manifest Data Files
        var metadata = await _metadataReader.LoadTableMetadataAsync(tableConfig.Location, cancellationToken).ConfigureAwait(false);
        var allDataFiles = await _metadataReader.LoadDataFilesAsync(metadata, tableConfig.Location, cancellationToken).ConfigureAwait(false);

        // SEC E-3 / E-5: honour the table's tenant column name (schema-derived).
        var TenantColumn = LakehouseLocationGuard.ResolveTenantColumn(metadata.CurrentSchema.Fields.Select(f => f.Name), DefaultTenantColumn);

        // 2. Inject Tenant Isolation Filter into query predicates (SEC EX-09: check caller tenant predicate instead of silently overwriting)
        var mergedPredicates = new Dictionary<string, string>(request.FilterPredicates, StringComparer.OrdinalIgnoreCase);
        var mandatoryColumns = new List<string>(1);
        if (!string.IsNullOrWhiteSpace(request.TenantId))
        {
            if (mergedPredicates.TryGetValue(TenantColumn, out var callerTenantPredicate))
            {
                var cleanPredicate = callerTenantPredicate.Trim();
                var expectedExact = $"== {request.TenantId}";
                if (!string.Equals(cleanPredicate, expectedExact, StringComparison.Ordinal) &&
                    !string.Equals(cleanPredicate, request.TenantId, StringComparison.Ordinal))
                {
                    throw new System.Security.SecurityException(
                        $"Tenant isolation violation: Supplied tenant predicate '{callerTenantPredicate}' does not match session tenant '{request.TenantId}'.");
                }
            }

            mergedPredicates[TenantColumn] = $"== {request.TenantId}";
            mandatoryColumns.Add(TenantColumn);
        }

        // 3. Vectorized Partition & Min/Max Stats Pruning (fail-closed for the tenant column)
        var matchingFiles = _partitionPruner.PruneDataFiles(allDataFiles, mergedPredicates, metadata.PartitionSpec, mandatoryColumns);
        var totalPruned = allDataFiles.Count - matchingFiles.Count;
        var efficiency = allDataFiles.Count > 0 ? (double)totalPruned / allDataFiles.Count * 100.0 : 0.0;

        // EXT-4: rows below are synthesized from file metadata, not read from Parquet; refuse outside demo mode (501).
        if (matchingFiles.Count > 0 && _demoData?.Enabled != true)
        {
            throw new Autheris.Domain.Exceptions.GatewayNotImplementedException("Reading lakehouse data files is not implemented; only sample rows are available with demo data enabled (Development).");
        }

        // 4. Generate/Scan Rows from matching data files (SEC EX-18: clamp limit by MaxScanRowsLimit)
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var maxLimit = _options.Value.Lakehouse.MaxScanRowsLimit > 0 ? _options.Value.Lakehouse.MaxScanRowsLimit : 50000;
        var limit = request.Limit > 0 ? Math.Min(request.Limit, maxLimit) : Math.Min(1000, maxLimit);

        foreach (var file in matchingFiles)
        {
            if (rows.Count >= limit) break;

            // Generate representative row records matching table schema and partition values
            var rowCountForFile = Math.Min(file.RecordCount, Math.Min(5, limit - rows.Count));
            for (int i = 0; i < rowCountForFile; i++)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var field in metadata.CurrentSchema.Fields)
                {
                    // If partition value is present, use it
                    if (file.PartitionValues.TryGetValue(field.Name, out var pVal))
                    {
                        row[field.Name] = pVal;
                        continue;
                    }

                    // SEC E-3: the tenant value is only ever a stored one (resolved below), never a synthesized sample.
                    if (string.Equals(field.Name, TenantColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Populate mock / scanned data based on field name and type (masking is applied by the callers)
                    row[field.Name] = GenerateSampleValue(field.Name, field.Type, i);
                }

                // SEC E-3: the tenant value of a row is the stored one (partition value or single-valued statistics) and is never
                // stamped with the session tenant. Rows without any stored tenant value are dropped.
                if (!string.IsNullOrWhiteSpace(request.TenantId))
                {
                    if (!row.ContainsKey(TenantColumn))
                    {
                        var stored = LakehouseLocationGuard.GetStoredTenantValue(file.PartitionValues, file.LowerBounds, file.UpperBounds, TenantColumn);
                        if (stored == null)
                        {
                            continue;
                        }

                        row[TenantColumn] = stored;
                    }

                    if (!LakehouseLocationGuard.RowBelongsToTenant(row, TenantColumn, request.TenantId))
                    {
                        _logger.LogWarning("Lakehouse scan of '{TableName}' dropped a row of a foreign tenant (row-level isolation).", request.TableName);
                        continue;
                    }
                }

                rows.Add(row);

                if (rows.Count >= limit) break;
            }
        }

        stopwatch.Stop();
        _logger.LogInformation("Lakehouse scan completed for '{TableName}': Scanned {Scanned}, Pruned {Pruned} ({Efficiency:F1}%), Rows: {RowsCount} in {ElapsedMs}ms.",
            request.TableName, matchingFiles.Count, totalPruned, efficiency, rows.Count, stopwatch.ElapsedMilliseconds);

        return new LakehouseScanResult(
            request.TableName,
            rows,
            matchingFiles.Count,
            totalPruned,
            efficiency,
            stopwatch.Elapsed
        );
    }

    private static object? GenerateSampleValue(string fieldName, string fieldType, int index)
    {
        var nameLower = fieldName.ToLowerInvariant();

        if (nameLower.Contains("id")) return $"ORD-2026-{1000 + index}";
        if (nameLower.Contains("email")) return $"user.{index}@acme-corp.com";
        if (nameLower.Contains("iban")) return "DE89 3704 0044 0532 0130 00";
        if (nameLower.Contains("amount")) return 150.75 + (index * 25.50);
        if (nameLower.Contains("status")) return index % 2 == 0 ? "COMPLETED" : "PROCESSING";
        if (nameLower.Contains("date")) return DateTime.UtcNow.AddDays(-index).ToString("yyyy-MM-dd");
        if (nameLower.Contains("health")) return "Hypertension Mild";

        return fieldType.ToLowerInvariant() switch
        {
            "int" or "integer" or "long" => 42 + index,
            "float" or "double" => 99.99,
            "boolean" => true,
            _ => $"Sample_{fieldName}_{index}"
        };
    }

    // R-POL-12: same rule set as the SQL paths (HASH included).
    private static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;
}
