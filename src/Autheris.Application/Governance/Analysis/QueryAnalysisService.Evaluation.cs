namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

public sealed partial class QueryAnalysisService
{
    private async Task EvaluateTableAndColumnsAsync(
        string rawDatasetName,
        IReadOnlyList<string> requestedColumns,
        IReadOnlyList<string> filterColumns,
        HashSet<string> providedFilterColumns,
        string queryName,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        AnalysisScopeContext scopeContext,
        IReadOnlyList<Consent> activeConsents,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        Dictionary<string, TableMetadata> tableLookup,
        bool selectAllColumns,
        SingleQueryAnalysisOutcome outcome,
        CancellationToken ct)
    {
        if (!tableLookup.TryGetValue(rawDatasetName, out var table))
        {
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.UnknownTable,
                $"Dataset '{rawDatasetName}' does not exist in the catalog.",
                QueryName: queryName,
                Dataset: rawDatasetName));
            return;
        }

        var canonicalDataset = FormatDatasetId(table.Identifier);
        var accessStatus = CatalogVisibility.ComputeAccessStatus(table.Identifier, activeConsents, pendingRequests: null, isAdmin: isAdmin);
        var hasTableGrant = string.Equals(accessStatus, "GRANTED", StringComparison.OrdinalIgnoreCase);

        var isOwner = false;
        if (_dataOwnershipRepository != null)
        {
            try
            {
                isOwner = await _dataOwnershipRepository.IsAuthorizedApproverForTableAsync(table.Identifier, effectiveSid, ct).ConfigureAwait(false);
            }
            catch
            {
                // Fail-safe
            }
        }

        if (table.Table.Visibility == DatasetVisibility.Hidden && !hasTableGrant && !isOwner && !isAdmin)
        {
            outcome.HiddenMissingDatasetsCount++;
            outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                QueryDiagnosticCodes.UnknownTable,
                "Referenced dataset does not exist or is not visible to the current principal.",
                QueryName: queryName));
            return;
        }

        if (!hasTableGrant && !isOwner && !isAdmin)
        {
            outcome.MissingConsents.Add(new RequestableConsentItem(
                Dataset: canonicalDataset,
                Column: null,
                RequestedLevel: "READ",
                SuggestedPurpose: $"Read access to dataset '{canonicalDataset}' required by query '{queryName}'."));

            // Table-level stop, independent of the catalog visibility value: without a table grant, ownership or administrator class the
            // column stage (select * expansion, OutputColumns, UnknownColumn, column-level requests) would disclose which columns exist.
            return;
        }

        var tableAllowConsents = activeConsents
            .Where(c => c.TableIdentifier == table.Identifier && c.Effect == ConsentEffect.Allow)
            .ToList();

        if (!string.IsNullOrWhiteSpace(table.Table.RlsExpression) ||
            tableAllowConsents.Any(c => c.RowFilters is { Count: > 0 } || !string.IsNullOrWhiteSpace(c.RowFilterCondition)))
        {
            outcome.HasRowFilters = true;
        }

        await EvaluateMandatoryVirtualFiltersAsync(
            table,
            canonicalDataset,
            providedFilterColumns,
            effectivePrincipal,
            effectiveSid,
            isAdmin,
            outcome,
            ct).ConfigureAwait(false);

        var columnLevels = ResolveTableColumnLevels(table, effectivePrincipal, effectiveSid, activeConsents, scopeContext);

        var columnsToInspect = selectAllColumns
            ? table.Columns.Select(c => c.ColumnName).ToList()
            : requestedColumns.ToList();

        foreach (var colName in columnsToInspect)
        {
            var catalogCol = table.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));

            if (catalogCol == null)
            {
                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.UnknownColumn,
                    $"Column '{colName}' does not exist on dataset '{canonicalDataset}'.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: colName));
                continue;
            }

            var accessLevel = ResolveEffectiveColumnAccess(
                catalogCol,
                columnLevels,
                isOwner,
                isAdmin);

            outcome.OutputColumns.Add(new AnalyzedOutputColumn(
                Dataset: canonicalDataset,
                Column: catalogCol.ColumnName,
                DataType: catalogCol.DataType,
                EffectiveAccessLevel: accessLevel));

            if (accessLevel == ColumnAccessLevel.Mask)
            {
                outcome.MissingConsents.Add(new RequestableConsentItem(
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName,
                    RequestedLevel: "READ_CLEARTEXT",
                    SuggestedPurpose: $"Unmasked cleartext access to column '{canonicalDataset}.{catalogCol.ColumnName}' for query '{queryName}'."));
            }

            CheckDeprecatedField(table.Identifier, canonicalDataset, catalogCol, sunsetRules, outcome.DeprecatedFields);
        }

        foreach (var filterColName in filterColumns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var catalogCol = table.Columns.FirstOrDefault(c =>
                string.Equals(c.ColumnName, filterColName, StringComparison.OrdinalIgnoreCase));

            if (catalogCol == null)
            {
                if (IsVirtualFilterParameter(table, canonicalDataset, filterColName))
                {
                    continue;
                }

                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.UnknownColumn,
                    $"Filter/join column '{filterColName}' does not exist on dataset '{canonicalDataset}'.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: filterColName));
                continue;
            }

            var filterAccess = ResolveEffectiveColumnAccess(
                catalogCol,
                columnLevels,
                isOwner,
                isAdmin);

            if (filterAccess == ColumnAccessLevel.Mask)
            {
                outcome.Diagnostics.Add(new QueryValidationDiagnostic(
                    QueryDiagnosticCodes.MaskedColumnInFilter,
                    $"Column '{canonicalDataset}.{catalogCol.ColumnName}' is masked for the target principal and cannot be used in a WHERE or JOIN predicate without cleartext consent.",
                    QueryName: queryName,
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName));

                outcome.MissingConsents.Add(new RequestableConsentItem(
                    Dataset: canonicalDataset,
                    Column: catalogCol.ColumnName,
                    RequestedLevel: "READ_CLEARTEXT",
                    SuggestedPurpose: $"Cleartext filter/join access on '{canonicalDataset}.{catalogCol.ColumnName}' required by query '{queryName}'."));
            }

            CheckDeprecatedField(table.Identifier, canonicalDataset, catalogCol, sunsetRules, outcome.DeprecatedFields);
        }
    }

    private async Task EvaluateMandatoryVirtualFiltersAsync(
        TableMetadata table,
        string canonicalDataset,
        HashSet<string> providedFilterColumns,
        ClaimsPrincipal effectivePrincipal,
        Sid effectiveSid,
        bool isAdmin,
        SingleQueryAnalysisOutcome outcome,
        CancellationToken ct)
    {
        IReadOnlyList<string>? requiredFilterCols = null;

        if (!_analysisOptions.MandatoryVirtualFilters.TryGetValue(canonicalDataset, out requiredFilterCols) &&
            !_analysisOptions.MandatoryVirtualFilters.TryGetValue($"{table.Identifier.Schema}.{table.Identifier.TableName}", out requiredFilterCols))
        {
            _analysisOptions.MandatoryVirtualFilters.TryGetValue(table.Identifier.TableName, out requiredFilterCols);
        }

        if (requiredFilterCols is { Count: > 0 })
        {
            outcome.HasMandatoryVirtualFilters = true;
            if (!isAdmin && requiredFilterCols.Any(req => !providedFilterColumns.Contains(req)))
            {
                outcome.IsBlockedByMandatoryFilter = true;
            }
        }

        if (_accessProfileRepository != null)
        {
            var tenantId = effectivePrincipal.GetTenantId();
            var profiles = await _accessProfileRepository.GetProfilesForSubjectAsync(tenantId, effectiveSid.Value, ct).ConfigureAwait(false);
            if (profiles.Count > 0)
            {
                outcome.HasMandatoryVirtualFilters = true;
            }
        }
    }

    private bool IsVirtualFilterParameter(TableMetadata table, string canonicalDataset, string filterColName)
    {
        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue(canonicalDataset, out var cols1) &&
            cols1.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue($"{table.Identifier.Schema}.{table.Identifier.TableName}", out var cols2) &&
            cols2.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (_analysisOptions.MandatoryVirtualFilters.TryGetValue(table.Identifier.TableName, out var cols3) &&
            cols3.Contains(filterColName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>Token scopes of the evaluated principal; <c>Scopes</c> is empty for an impersonated target.</summary>
    private sealed record AnalysisScopeContext(IReadOnlySet<string> Scopes, bool TargetImpersonated)
    {
        /// <summary>Levels per table for the principal of this request (queries of one request run sequentially).</summary>
        public Dictionary<TableIdentifier, IReadOnlyDictionary<string, ColumnAccessLevel>> LevelCache { get; } = new();
    }

    private static ColumnAccessLevel ResolveEffectiveColumnAccess(
        TableColumn column,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnLevels,
        bool isOwner,
        bool isAdmin)
    {
        if (isAdmin || isOwner)
        {
            return ColumnAccessLevel.Clear;
        }

        return columnLevels.TryGetValue(column.ColumnName, out var level) ? level : ColumnAccessLevel.Deny;
    }

    /// <summary>
    /// Issue #127: predicted level per column, resolved once per table by the data-path resolver so the analysis never
    /// promises more than the execution delivers. Consents are restricted to the principal's tenant. An impersonated target has
    /// no known token: the prediction is the minimum of the evaluation without scopes and the evaluation with every scope named
    /// by a rule of the table. A resolver failure denies every column.
    /// </summary>
    private IReadOnlyDictionary<string, ColumnAccessLevel> ResolveTableColumnLevels(
        TableMetadata table,
        ClaimsPrincipal principal,
        Sid sid,
        IReadOnlyList<Consent> activeConsents,
        AnalysisScopeContext scopeContext)
    {
        if (scopeContext.LevelCache.TryGetValue(table.Identifier, out var cached))
        {
            return cached;
        }

        Dictionary<string, ColumnAccessLevel> levels;
        try
        {
            var tenantId = principal.GetTenantId();
            var tenantConsents = activeConsents.Where(c => c.TenantId == tenantId).ToList();
            var groups = principal.GetGroupSids();
            var roles = principal.GetUserRoles();

            var evaluations = new List<IReadOnlySet<string>> { scopeContext.Scopes };
            if (scopeContext.TargetImpersonated)
            {
                var worstCase = CatalogColumnAccessResolver.AllRuleScopes(table, tenantConsents);
                if (worstCase.Count > 0)
                {
                    evaluations.Add(worstCase);
                }

                // REQ-GOV-143 (SEC-GOV143-12): the target may hold a restricted scope; predict that case too (only while the restriction is
                // enabled). The minimum across evaluations keeps the prediction on the safe side.
                if (_restrictedScopes.Scopes.Count > 0)
                {
                    evaluations.Add(_restrictedScopes.Scopes);
                }
            }

            levels = CatalogColumnAccessResolver.ResolveGranted(_consentResolution, table, sid, groups, roles, tenantConsents, evaluations[0]);
            for (var i = 1; i < evaluations.Count; i++)
            {
                var other = CatalogColumnAccessResolver.ResolveGranted(_consentResolution, table, sid, groups, roles, tenantConsents, evaluations[i]);
                foreach (var (column, level) in other)
                {
                    if (level < levels[column])
                    {
                        levels[column] = level;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Fail closed; only the table and the exception type are logged (no consent content, scopes or values).
            _logger?.LogWarning("Column level prediction failed for {Table} ({ExceptionType}); all columns denied.", table.Identifier.ToString(), ex.GetType().Name);
            levels = CatalogColumnAccessResolver.AllDeny(table);
        }

        CatalogColumnAccessResolver.ApplyCatalogDenyRules(levels, table);
        scopeContext.LevelCache[table.Identifier] = levels;
        return levels;
    }

    private static void CheckDeprecatedField(
        TableIdentifier tableId,
        string canonicalDataset,
        TableColumn column,
        IReadOnlyList<FieldSunsettingRule> sunsetRules,
        List<DeprecatedFieldReference> deprecatedFields)
    {
        var dep = Autheris.Application.Mcp.Services.McpDatasetCatalog.ResolveFieldDeprecation(tableId, column, sunsetRules);
        if (dep != null)
        {
            deprecatedFields.Add(new DeprecatedFieldReference(
                Dataset: canonicalDataset,
                Column: column.ColumnName,
                DeprecatedSince: dep.DeprecatedSince,
                SunsetDate: dep.SunsetDate,
                ReplacementField: dep.ReplacementField,
                Reason: dep.Reason));
        }
    }

    private static void MergeOutcome(
        SingleQueryAnalysisOutcome source,
        List<QueryValidationDiagnostic> diagnostics,
        List<AnalyzedOutputColumn> outputColumns,
        HashSet<string> outputColumnKeys,
        List<DeprecatedFieldReference> deprecatedFields,
        HashSet<string> deprecatedKeys,
        List<RequestableConsentItem> missingConsents,
        HashSet<string> missingConsentKeys,
        Dictionary<string, int> rootFieldCosts,
        ref bool hasRowFilters,
        ref bool hasMandatoryVirtualFilters,
        ref bool isBlockedByMandatoryFilter,
        ref int hiddenMissingDatasetsCount,
        ref int totalEstimatedCost,
        List<AnalyzedFederatedTable>? federatedTables = null)
    {
        diagnostics.AddRange(source.Diagnostics);

        if (federatedTables != null && source.FederatedTables.Count > 0)
        {
            federatedTables.AddRange(source.FederatedTables);
        }

        foreach (var (field, cost) in source.RootFieldCosts)
        {
            rootFieldCosts[field] = rootFieldCosts.TryGetValue(field, out var existing)
                ? (int)Math.Min((long)int.MaxValue, (long)existing + cost)
                : cost;
        }

        foreach (var col in source.OutputColumns)
        {
            var key = $"{col.Dataset}.{col.Column}";
            if (outputColumnKeys.Add(key))
            {
                outputColumns.Add(col);
            }
        }

        foreach (var dep in source.DeprecatedFields)
        {
            var key = $"{dep.Dataset}.{dep.Column}";
            if (deprecatedKeys.Add(key))
            {
                deprecatedFields.Add(dep);
            }
        }

        foreach (var mc in source.MissingConsents)
        {
            var key = $"{mc.Dataset}:{mc.Column ?? "*"}:{mc.RequestedLevel}";
            if (missingConsentKeys.Add(key))
            {
                missingConsents.Add(mc);
            }
        }

        if (source.HasRowFilters) hasRowFilters = true;
        if (source.HasMandatoryVirtualFilters) hasMandatoryVirtualFilters = true;
        if (source.IsBlockedByMandatoryFilter) isBlockedByMandatoryFilter = true;
        hiddenMissingDatasetsCount += source.HiddenMissingDatasetsCount;
        totalEstimatedCost += source.EstimatedCost;
    }

    private sealed class SingleQueryAnalysisOutcome
    {
        public List<string> ExtractedTables { get; } = new();
        public List<QueryValidationDiagnostic> Diagnostics { get; } = new();
        public List<AnalyzedOutputColumn> OutputColumns { get; } = new();
        public List<DeprecatedFieldReference> DeprecatedFields { get; } = new();
        public List<RequestableConsentItem> MissingConsents { get; } = new();
        public Dictionary<string, int> RootFieldCosts { get; } = new(StringComparer.Ordinal);
        public List<AnalyzedFederatedTable> FederatedTables { get; } = new();
        public bool HasRowFilters { get; set; }
        public bool HasMandatoryVirtualFilters { get; set; }
        public bool IsBlockedByMandatoryFilter { get; set; }
        public int HiddenMissingDatasetsCount { get; set; }
        public int EstimatedCost { get; set; }
    }
}
