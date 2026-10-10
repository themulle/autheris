namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// F-GOV-14 (WI-6; M5; SEC-12, SEC-13, SEC-23): plans the protected HTTP tables of a cross-source query.
/// <list type="number">
/// <item>Every (filter, binding) pair of every protected table is validated (target columns exist, key types are
/// comparable, structured filter without validity window) before any key is loaded.</item>
/// <item>All key sets are loaded (bounded by the caller's cancellation token, also when the provider ignores it);
/// every failure is a stable code, a provider failure is <c>VIRTUAL_FILTER_SOURCE_UNAVAILABLE</c> without its message.</item>
/// <item>An empty key set is Tier 0 (skip the upstream call); otherwise the strategies decide: <c>ShortCircuitOnly</c>
/// denies a query that is not a point lookup, <c>PushdownOnly</c> denies (pushdown is not available yet),
/// everything else reads the table completely (Tier 3).</item>
/// </list>
/// Every plan carries the enforcement, so what is returned never depends on the tier.
/// </summary>
public sealed class VirtualFilterFederationPlanner : IVirtualFilterFederationPlanner
{
    private readonly IVirtualFilterKeySetProvider _keySets;

    public VirtualFilterFederationPlanner(IVirtualFilterKeySetProvider keySets)
    {
        _keySets = keySets ?? throw new ArgumentNullException(nameof(keySets));
    }

    /// <summary>
    /// True when the table is an HTTP table whose decision carries a Virtual Filter (predicate, names or pairs); the
    /// gateway must enforce it itself because the upstream cannot evaluate it.
    /// </summary>
    internal static bool RequiresEnforcement(TableMetadata table, TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);
        return table.Table?.DataSourceType == DataSourceType.HttpDeclarative &&
               (decision.MandatoryRowPredicateSql != null ||
                decision.AppliedVirtualFilters is { Count: > 0 } ||
                decision.AppliedVirtualFilterBindings is { Count: > 0 });
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>> PlanAsync(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        TenantId tenant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        // 1. Validate every pair of every table before any key is loaded (fail closed, no egress for a broken profile).
        var prepared = new List<PreparedTable>();
        var seen = new HashSet<TableIdentifier>();
        foreach (var table in tables)
        {
            if (!seen.Add(CrossSourcePlanner.DecisionKey(table)))
            {
                continue;   // self-join: one plan per table
            }

            var decision = CrossSourcePlanner.RequireDecision(decisions, table);
            if (!RequiresEnforcement(table.Metadata, decision))
            {
                continue;
            }

            var pairs = decision.AppliedVirtualFilterBindings;
            if (pairs is not { Count: > 0 })
            {
                throw new GatewaySecurityException(
                    $"The Virtual Filters of '{table.Metadata.Identifier}' cannot be enforced: the decision carries no bindings.",
                    VirtualFilterErrorCodes.EnforcementUnavailable);
            }

            var keyTypes = new VirtualFilterKeyType[pairs.Count][];
            for (int i = 0; i < pairs.Count; i++)
            {
                var columns = VirtualFilterEnforcement.TargetColumns(table.Metadata, pairs[i]);
                keyTypes[i] = VirtualFilterEnforcement.KeyTypes(table.Metadata, columns);
            }

            prepared.Add(new PreparedTable(table.Metadata, pairs, keyTypes));
        }

        // 2. Load all key sets; the caller's token bounds the whole step (SEC-12).
        var plans = new Dictionary<TableIdentifier, VirtualFilterTablePlan>();
        foreach (var table in prepared)
        {
            var loaded = new List<(AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)>(table.Pairs.Count);
            for (int i = 0; i < table.Pairs.Count; i++)
            {
                var keys = await LoadAsync(table.Pairs[i].Filter, table.KeyTypes[i], tenant, ct).ConfigureAwait(false);
                loaded.Add((table.Pairs[i], keys));
            }

            plans[table.Metadata.Identifier] = Decide(statement, table, loaded);
        }

        return plans;
    }

    private static VirtualFilterTablePlan Decide(
        SqlStatement? statement,
        PreparedTable table,
        IReadOnlyList<(AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)> loaded)
    {
        var enforcement = VirtualFilterEnforcement.Create(table.Metadata, loaded);

        // Tier 0: nothing can be authorized, whatever the strategy; the read path still runs all guards.
        if (loaded.Any(l => l.Keys.Count == 0))
        {
            return new VirtualFilterTablePlan(enforcement, VirtualFilterTier.EmptyKeySet, skipUpstream: true, "empty_key_set");
        }

        // No point lookup is recognized yet (WI-7), so every query is a scan; pushdown (WI-8) does not exist yet.
        _ = statement;
        var strategies = table.Pairs.Select(p => p.Binding.Strategy).ToHashSet();
        if (strategies.Contains(VirtualFilterExecutionStrategy.ShortCircuitOnly))
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter bindings of '{table.Metadata.Identifier}' permit point lookups only; the query is a scan.",
                VirtualFilterErrorCodes.ScanNotPermitted);
        }

        if (strategies.Contains(VirtualFilterExecutionStrategy.PushdownOnly))
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter bindings of '{table.Metadata.Identifier}' require a key pushdown, which is not available.",
                VirtualFilterErrorCodes.PushdownUnavailable);
        }

        return new VirtualFilterTablePlan(enforcement, VirtualFilterTier.FullFetch, skipUpstream: false, "full_fetch");
    }

    private async Task<VirtualFilterKeySet> LoadAsync(
        VirtualFilter filter,
        IReadOnlyList<VirtualFilterKeyType> keyTypes,
        TenantId tenant,
        CancellationToken ct)
    {
        try
        {
            return await _keySets.GetAsync(filter, keyTypes, tenant, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (GatewaySecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Includes cancellation and timeout; the inner exception is attached, never concatenated (SEC-11).
            throw new GatewaySecurityException(
                $"The key source of the Virtual Filter '{filter.Name}' is unavailable.", VirtualFilterErrorCodes.SourceUnavailable, ex);
        }
    }

    private sealed record PreparedTable(
        TableMetadata Metadata,
        IReadOnlyList<AppliedVirtualFilterBinding> Pairs,
        VirtualFilterKeyType[][] KeyTypes);
}
