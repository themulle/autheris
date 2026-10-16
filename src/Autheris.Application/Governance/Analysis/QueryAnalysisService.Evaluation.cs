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
                table,
                catalogCol,
                activeConsents,
                hasTableGrant,
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
                table,
                catalogCol,
                activeConsents,
                hasTableGrant,
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

    private static ColumnAccessLevel ResolveEffectiveColumnAccess(
        TableMetadata table,
        TableColumn column,
        IReadOnlyList<Consent> activeConsents,
        bool hasTableGrant,
        bool isOwner,
        bool isAdmin)
    {
        if (isAdmin || isOwner)
        {
            return ColumnAccessLevel.Clear;
        }

        var tableConsents = activeConsents
            .Where(c => c.TableIdentifier == table.Identifier)
            .ToList();

        var allowConsents = tableConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .ToList();

        bool isExplicitlyDenied = tableConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       cr.AccessLevel == ColumnAccessLevel.Deny) ||
            (table.ColumnMaskingRules != null &&
             table.ColumnMaskingRules.TryGetValue(column.ColumnName, out var mr) &&
             string.Equals(mr.RuleType, "DENY", StringComparison.OrdinalIgnoreCase));

        if (isExplicitlyDenied)
        {
            return ColumnAccessLevel.Deny;
        }

        // Fail closed (issue #116): the analysis has no caller scopes, so a scope-specific rule never counts as a grant.
        bool hasExplicitClear = allowConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       !Autheris.Application.Services.ConsentResolutionService.IsScopeSpecific(cr) &&
                       cr.AccessLevel == ColumnAccessLevel.Clear);

        bool hasExplicitMask = allowConsents
            .SelectMany(c => c.ColumnRules)
            .Any(cr => string.Equals(cr.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                       cr.AccessLevel == ColumnAccessLevel.Mask);

        bool isSensitiveInCatalog = column.IsSensitive ||
                                    (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(column.ColumnName));

        if ((isSensitiveInCatalog && !hasExplicitClear) || (hasExplicitMask && !hasExplicitClear))
        {
            return ColumnAccessLevel.Mask;
        }

        return hasTableGrant ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny;
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
        ref bool hasRowFilters,
        ref bool hasMandatoryVirtualFilters,
        ref bool isBlockedByMandatoryFilter,
        ref int hiddenMissingDatasetsCount,
        ref int totalEstimatedCost)
    {
        diagnostics.AddRange(source.Diagnostics);

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
        public bool HasRowFilters { get; set; }
        public bool HasMandatoryVirtualFilters { get; set; }
        public bool IsBlockedByMandatoryFilter { get; set; }
        public int HiddenMissingDatasetsCount { get; set; }
        public int EstimatedCost { get; set; }
    }
}
