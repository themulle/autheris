namespace Autheris.Application.Sql.Cost;

using System;
using System.Collections.Generic;
using Autheris.Application.Governance.Analysis;
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
            return (int)Math.Clamp(dmlCost, 1, int.MaxValue);
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
        if (total >= int.MaxValue) return int.MaxValue;
        if (total < 1) return 1;
        return (int)total;
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
