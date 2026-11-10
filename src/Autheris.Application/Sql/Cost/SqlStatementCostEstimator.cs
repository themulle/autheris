namespace Autheris.Application.Sql.Cost;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql.Indexing;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Analysis;

public static class SqlStatementCostEstimator
{
    public static int EstimateCost(
        SqlQueryMetadata metadata,
        SqlRowLimit rowLimit,
        QueryCostOptions costOptions,
        QueryAnalysisOptions analysisOptions,
        WebSqlCostOptions webSqlCostOptions,
        Func<string, long?>? getEstimatedRowCount = null,
        long pageOffset = 0,
        bool isPaged = false,
        long maxAffectedRows = 1000,
        Func<string, TableMetadata?>? getTableMetadata = null,
        Func<string, IReadOnlySet<string>?>? getDeniedColumns = null,
        Action<IReadOnlyList<WebSqlIndexHint>>? onIndexHints = null)
    {
        var (cost, hints) = EstimateCostWithHints(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCostOptions,
            getEstimatedRowCount,
            getTableMetadata,
            getDeniedColumns,
            pageOffset,
            isPaged,
            maxAffectedRows);

        onIndexHints?.Invoke(hints);
        return cost;
    }

    public static (int Cost, IReadOnlyList<WebSqlIndexHint> IndexHints) EstimateCostWithHints(
        SqlQueryMetadata metadata,
        SqlRowLimit rowLimit,
        QueryCostOptions costOptions,
        QueryAnalysisOptions analysisOptions,
        WebSqlCostOptions webSqlCostOptions,
        Func<string, long?>? getEstimatedRowCount = null,
        Func<string, TableMetadata?>? getTableMetadata = null,
        Func<string, IReadOnlySet<string>?>? getDeniedColumns = null,
        long pageOffset = 0,
        bool isPaged = false,
        long maxAffectedRows = 1000)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rowLimit);
        costOptions ??= new QueryCostOptions();
        analysisOptions ??= new QueryAnalysisOptions();
        webSqlCostOptions ??= new WebSqlCostOptions();

        if (metadata.StatementType is SqlStatementType.Insert or SqlStatementType.Update or SqlStatementType.Delete)
        {
            long effectiveMax = maxAffectedRows > 0 ? maxAffectedRows : 1000;
            long dmlCost = (long)costOptions.BaseCost + webSqlCostOptions.DmlStatementCost + (long)Math.Ceiling(costOptions.RowCostFactor * effectiveMax);
            return ((int)Math.Clamp(dmlCost, 1, int.MaxValue), Array.Empty<WebSqlIndexHint>());
        }

        long effectiveRows = isPaged ? rowLimit.DefaultMaxRows : rowLimit.Effective(metadata.ExplicitLimitValue);
        double rProduct = costOptions.RowCostFactor * Math.Max(0, effectiveRows);
        long rTerm = (rProduct >= int.MaxValue || double.IsInfinity(rProduct) || double.IsNaN(rProduct))
            ? int.MaxValue
            : (long)Math.Ceiling(rProduct);

        long crossJoinSumK = 0;
        if (metadata.CrossJoins != null && metadata.CrossJoins.Count > 0)
        {
            foreach (var cj in metadata.CrossJoins)
            {
                if (cj.IsUnknownCardinality)
                {
                    crossJoinSumK += 4;
                    continue;
                }

                long? leftRows = cj.LeftTableOrAlias != null && getEstimatedRowCount != null ? getEstimatedRowCount(cj.LeftTableOrAlias) : null;
                long? rightRows = cj.RightTableOrAlias != null && getEstimatedRowCount != null ? getEstimatedRowCount(cj.RightTableOrAlias) : null;

                bool leftSmall = leftRows.HasValue && leftRows.Value <= 1000;
                bool rightSmall = rightRows.HasValue && rightRows.Value <= 1000;

                int k = (leftSmall && rightSmall) ? 1 : 4;
                crossJoinSumK += k;
            }
        }
        else if (metadata.CrossJoinCount > 0)
        {
            crossJoinSumK += (long)metadata.CrossJoinCount * 4;
        }

        int j = Math.Max(0, metadata.JoinCount);
        int x = Math.Max(0, metadata.CrossJoinCount);
        int explicitJoins = Math.Max(0, j - x);

        long joinTerm = (long)explicitJoins * analysisOptions.CostPerJoin;
        long crossJoinTerm = (long)webSqlCostOptions.CrossJoinCost * crossJoinSumK;
        long subqueryTerm = (long)Math.Max(0, metadata.MaxSubqueryDepth) * analysisOptions.CostPerSubqueryDepth;
        long setOpTerm = (long)Math.Max(0, metadata.SetOperationCount) * webSqlCostOptions.SetOperationCost;

        bool bounded = metadata.HasExplicitLimit || isPaged;
        long unboundedPenalty = bounded ? 0 : analysisOptions.UnboundedScanPenalty;

        long recursivePenalty = metadata.HasRecursiveCte
            ? (long)analysisOptions.UnboundedScanPenalty + (4L * webSqlCostOptions.CrossJoinCost)
            : 0;

        long offsetTerm = (pageOffset > 0 && webSqlCostOptions.OffsetRowsPerCostUnit > 0)
            ? (long)Math.Ceiling((double)pageOffset / webSqlCostOptions.OffsetRowsPerCostUnit)
            : 0;

        long total = (long)costOptions.BaseCost + rTerm + joinTerm + crossJoinTerm + subqueryTerm + setOpTerm + unboundedPenalty + recursivePenalty + offsetTerm;

        var indexHints = new List<WebSqlIndexHint>();
        int unindexedScans = 0;

        if (getTableMetadata != null && metadata.ReferencedTables is { Count: > 0 })
        {
            foreach (var target in metadata.ReferencedTables)
            {
                var tableMeta = getTableMetadata(target.FullName) ?? getTableMetadata(target.TableName);
                if (tableMeta == null) continue;

                var filterCols = new List<string>();
                if (metadata.FilterColumnReferences is { Count: > 0 })
                {
                    foreach (var fc in metadata.FilterColumnReferences)
                    {
                        if (SqlDataSourceResolver.ReferencesColumn(fc.TableOrAlias, fc.ColumnName, fc.ColumnName, target))
                        {
                            if (tableMeta.Columns.Any(c => string.Equals(c.ColumnName, fc.ColumnName, StringComparison.OrdinalIgnoreCase)) &&
                                !filterCols.Contains(fc.ColumnName, StringComparer.OrdinalIgnoreCase))
                            {
                                filterCols.Add(fc.ColumnName);
                            }
                        }
                    }
                }

                var groupByCols = new List<string>();
                if (metadata.GroupByColumnReferences is { Count: > 0 })
                {
                    foreach (var gc in metadata.GroupByColumnReferences)
                    {
                        if (SqlDataSourceResolver.ReferencesColumn(gc.TableOrAlias, gc.ColumnName, gc.ColumnName, target))
                        {
                            if (tableMeta.Columns.Any(c => string.Equals(c.ColumnName, gc.ColumnName, StringComparison.OrdinalIgnoreCase)) &&
                                !groupByCols.Contains(gc.ColumnName, StringComparer.OrdinalIgnoreCase))
                            {
                                groupByCols.Add(gc.ColumnName);
                            }
                        }
                    }
                }

                bool hasFilter = filterCols.Count > 0;
                bool hasGroupBy = groupByCols.Count > 0;

                if (!hasFilter && !hasGroupBy)
                {
                    continue;
                }

                var candidates = IndexCandidates.GetCandidateIndexes(tableMeta);

                bool filterMatched = !hasFilter || candidates.Any(c => c.Columns.Count > 0 &&
                    filterCols.Any(fc => string.Equals(fc, c.Columns[0], StringComparison.OrdinalIgnoreCase)));

                bool groupMatched = !hasGroupBy || candidates.Any(c =>
                    c.Columns.Count >= groupByCols.Count &&
                    groupByCols.All(gc => c.Columns.Take(groupByCols.Count).Any(col => string.Equals(col, gc, StringComparison.OrdinalIgnoreCase))));

                bool isUsable = filterMatched && groupMatched;

                if (!isUsable)
                {
                    unindexedScans++;

                    var helpful = new List<string>();
                    if (!filterMatched) helpful.AddRange(filterCols);
                    if (!groupMatched)
                    {
                        foreach (var gc in groupByCols)
                        {
                            if (!helpful.Contains(gc, StringComparer.OrdinalIgnoreCase))
                                helpful.Add(gc);
                        }
                    }

                    var denied = getDeniedColumns?.Invoke(target.FullName) ?? getDeniedColumns?.Invoke(target.TableName);
                    bool anyDenied = denied != null && helpful.Any(c => denied.Contains(c));

                    IReadOnlyList<string> helpfulColumns = anyDenied ? Array.Empty<string>() : helpful.AsReadOnly();
                    indexHints.Add(new WebSqlIndexHint(target.TableName, "NO_USABLE_INDEX", helpfulColumns));
                }
            }
        }

        if (unindexedScans > 0 && costOptions.UnindexedRootListSurchargeMultiplier > 1.0)
        {
            for (int i = 0; i < unindexedScans; i++)
            {
                total = (long)Math.Ceiling(total * costOptions.UnindexedRootListSurchargeMultiplier);
            }
        }
        else if (costOptions.UnindexedRootListSurchargeMultiplier <= 1.0)
        {
            indexHints.Clear();
        }

        if (total >= int.MaxValue) return (int.MaxValue, indexHints);
        if (total < 1) return (1, indexHints);
        return ((int)total, indexHints);
    }

    public static int CalculateSettlementCost(
        long rowsDeliveredOrAffected,
        double elapsedSeconds,
        QueryCostOptions costOptions,
        WebSqlCostOptions webSqlCostOptions,
        bool isFailureOrAbort = false)
    {
        costOptions ??= new QueryCostOptions();
        webSqlCostOptions ??= new WebSqlCostOptions();

        long baseCost = costOptions.BaseCost;
        long rowTerm = (long)Math.Ceiling(costOptions.RowCostFactor * Math.Max(0, rowsDeliveredOrAffected));
        long elapsedTerm = (long)Math.Ceiling(webSqlCostOptions.ExecutionSecondCost * Math.Max(0, elapsedSeconds));

        long actual = baseCost + rowTerm + elapsedTerm;
        actual = Math.Clamp(actual, 1, int.MaxValue);

        if (isFailureOrAbort)
        {
            long failureFee = costOptions.FailureFee;
            return (int)Math.Max(failureFee, actual);
        }

        return (int)actual;
    }
}
