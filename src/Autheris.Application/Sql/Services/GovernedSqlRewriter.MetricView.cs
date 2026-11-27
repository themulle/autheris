namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

public sealed partial class GovernedSqlRewriter
{
    private async Task<(string Sql, MetricViewGuardResult Guard)> EnforceMetricViewGuardAsync(
        string rawSql,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        DatabaseDialect targetDatabaseDialect,
        TenantId tenantId,
        CancellationToken ct)
    {
        int minGroupSize = 0;
        var metricViews = decidedTableMetadata.Values.Where(static t => t.Table.ObjectKind == DiscoveryObjectKind.MetricView).ToList();
        if (metricViews.Count == 1)
        {
            minGroupSize = await ResolveMetricViewMinGroupSizeAsync(metricViews[0], tenantId, ct).ConfigureAwait(false);
        }

        var guard = MetricViewStatementGuard.Enforce(
            rawSql,
            decidedTableMetadata,
            decisions,
            targetDatabaseDialect,
            minGroupSize,
            _analyticsOptions.Privacy.RestrictNegatedFilters);
        return (guard.AugmentedSql ?? rawSql, guard);
    }

    /// <summary>Resolves the minimum group size of a metric view exactly like the governed aggregation path (no request override).</summary>
    internal async Task<int> ResolveMetricViewMinGroupSizeAsync(TableMetadata metricView, TenantId tenantId, CancellationToken ct)
    {
        var floor = GovernedAggregationService.BuildOperatorFloor(_analyticsOptions, hasExplicitOptions: true, metricView.Identifier);
        // Without a provider catalog lowering is deliberately refused (stricter than the aggregation path).
        var protection = _protectionProvider is null
            ? GovernedAggregationService.ProtectionUnavailable()
            : await GovernedAggregationService.LoadProtectionFailClosedAsync(_protectionProvider, tenantId, metricView.Identifier, ct).ConfigureAwait(false);
        return GovernedAggregationService.Resolve(metricView, null, floor, _analyticsOptions.Privacy, _logger, protection).Value;
    }

    private static IReadOnlySet<string> ApplyMetricViewAllowedFunctions(
        IReadOnlySet<string> allowedFunctions,
        TargetSqlDialect targetSqlDialect,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata)
    {
        if (targetSqlDialect != TargetSqlDialect.Databricks ||
            !decidedTableMetadata.Values.Any(static t => t.Table.ObjectKind == DiscoveryObjectKind.MetricView))
        {
            return allowedFunctions;
        }

        return new HashSet<string>(allowedFunctions, StringComparer.OrdinalIgnoreCase) { "measure" };
    }
}
