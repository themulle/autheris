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
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// F-GOV-14 (WI-6; M5; SEC-12, SEC-13, SEC-23): plans the protected HTTP tables of a cross-source query.
/// <list type="number">
/// <item>Every (filter, binding) pair of every protected table is validated (target columns exist, key types are
/// comparable, structured filter without validity window) before any key is loaded.</item>
/// <item>All key sets are loaded (bounded by the caller's cancellation token, also when the provider ignores it);
/// every failure is a stable code, a provider failure is <c>VIRTUAL_FILTER_SOURCE_UNAVAILABLE</c> without its message.</item>
/// <item>An empty key set is Tier 0 and a point lookup without any authorized queried key is Tier 1 (both skip the
/// upstream call); otherwise the strategies decide (see <see cref="VirtualFilterPushdownSelector"/>): the authorized
/// (and queried) keys are pushed when the endpoint declares a safe capability within budget (Tier 1 hit / Tier 2),
/// <c>ShortCircuitOnly</c> denies a query that is not a point lookup, <c>PushdownOnly</c> and a <c>ShortCircuitOnly</c>
/// hit deny when no pushdown is possible, everything else reads the table completely (Tier 3).</item>
/// </list>
/// Every plan carries the enforcement, so what is returned never depends on the tier.
/// </summary>
public sealed class VirtualFilterFederationPlanner : IVirtualFilterFederationPlanner
{
    private readonly IVirtualFilterKeySetProvider _keySets;
    private readonly IOptions<GatewayOptions>? _options;

    public VirtualFilterFederationPlanner(IVirtualFilterKeySetProvider keySets, IOptions<GatewayOptions>? options = null)
    {
        _keySets = keySets ?? throw new ArgumentNullException(nameof(keySets));
        _options = options;
    }

    private VirtualFilterFederationOptions Federation => _options?.Value.VirtualFilters?.Federation ?? new VirtualFilterFederationOptions();

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
        CancellationToken ct,
        ICollection<VirtualFilterKeySourceRead>? keySourceReads = null)
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
            var columnsPerPair = new string[pairs.Count][];
            for (int i = 0; i < pairs.Count; i++)
            {
                columnsPerPair[i] = VirtualFilterEnforcement.TargetColumns(table.Metadata, pairs[i]);
                keyTypes[i] = VirtualFilterEnforcement.KeyTypes(table.Metadata, columnsPerPair[i]);
            }

            prepared.Add(new PreparedTable(table.Metadata, pairs, keyTypes, columnsPerPair));
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
                keySourceReads?.Add(new VirtualFilterKeySourceRead(table.Metadata.Identifier, KeySetAudit(table.Pairs[i], keys)));
            }

            var audits = loaded.Select(l => KeySetAudit(l.Pair, l.Keys)).ToList();
            plans[table.Metadata.Identifier] = Decide(statement, tables, table, loaded).WithKeySets(audits);
        }

        return plans;
    }

    private static VirtualFilterKeySetAudit KeySetAudit(AppliedVirtualFilterBinding pair, VirtualFilterKeySet keys) =>
        new(pair.Filter.Name, pair.Filter.Source, pair.Binding.ComputeHash(), pair.Binding.Strategy.ToString(), keys.Count, keys.DroppedKeyCount, keys.CacheHit);

    private VirtualFilterTablePlan Decide(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        PreparedTable table,
        IReadOnlyList<(AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)> loaded)
    {
        var plan = DecideTier(statement, tables, table, loaded);

        // CR-01: what is fetched must never depend on a request the gateway cannot form. A read of a path template that
        // keeps a placeholder (a full scan, an unsafe or over-budget key set, the switch off, a second placeholder) would go
        // upstream with the braces unexpanded and answer 404 or 400, or worse, something else. It is denied before any call.
        if (!plan.SkipUpstream && HttpKeyPushdownCapability.HasUnfillablePlaceholder(table.Metadata.HttpEndpoint, plan.PathLookup?.Argument))
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter bindings of '{table.Metadata.Identifier}' need a read of a path template that cannot be filled with the authorized keys.",
                VirtualFilterErrorCodes.PathTemplateUnfillable);
        }

        return plan;
    }

    private VirtualFilterTablePlan DecideTier(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        PreparedTable table,
        IReadOnlyList<(AppliedVirtualFilterBinding Pair, VirtualFilterKeySet Keys)> loaded)
    {
        var enforcement = VirtualFilterEnforcement.Create(table.Metadata, loaded);

        // Tier 0: nothing can be authorized, whatever the strategy; the read path still runs all guards.
        if (loaded.Any(l => l.Keys.Count == 0))
        {
            return new VirtualFilterTablePlan(enforcement, VirtualFilterTier.EmptyKeySet, skipUpstream: true, "empty_key_set");
        }

        // Tier 1: a point lookup without any authorized queried key for some pair cannot return a row. The upstream
        // call is skipped and an empty relation is staged, so the user query still yields its true answer.
        var federation = Federation;
        var queried = federation.PushdownEnabled ? QueriedKeys(statement, tables, table, federation.MaxPointLookupTuples) : null;
        if (queried != null)
        {
            for (int i = 0; i < queried.Length; i++)
            {
                if (queried[i] is { } keys && !keys.Any(loaded[i].Keys.Contains))
                {
                    return new VirtualFilterTablePlan(enforcement, VirtualFilterTier.PointLookup, skipUpstream: true, "point_lookup_miss");
                }
            }
        }

        bool isPointLookup = queried != null && queried.All(q => q != null);
        var strategies = table.Pairs.Select(p => p.Binding.Strategy).ToHashSet();
        bool shortCircuitOnly = strategies.Contains(VirtualFilterExecutionStrategy.ShortCircuitOnly);
        if (shortCircuitOnly && !isPointLookup)
        {
            throw new GatewaySecurityException(
                $"The Virtual Filter bindings of '{table.Metadata.Identifier}' permit point lookups only; the query is a scan.",
                VirtualFilterErrorCodes.ScanNotPermitted);
        }

        // A hit of ShortCircuitOnly and every PushdownOnly binding need the keys pushed; DuckDbHashJoin (or the global
        // switch) forbids pushing. Contradictions deny instead of choosing one side (most restrictive wins).
        bool needsPushdown = shortCircuitOnly || strategies.Contains(VirtualFilterExecutionStrategy.PushdownOnly);
        if (!federation.PushdownEnabled || strategies.Contains(VirtualFilterExecutionStrategy.DuckDbHashJoin))
        {
            return needsPushdown
                ? throw PushdownDenied(table, VirtualFilterErrorCodes.PushdownUnavailable)
                : new VirtualFilterTablePlan(enforcement, VirtualFilterTier.FullFetch, skipUpstream: false, "full_fetch");
        }

        var pushPairs = new List<PushdownPair>(loaded.Count);
        for (int i = 0; i < loaded.Count; i++)
        {
            pushPairs.Add(new PushdownPair(loaded[i].Pair, table.Columns[i], loaded[i].Keys, queried?[i]));
        }

        var candidate = VirtualFilterPushdownSelector.Select(table.Metadata, pushPairs, federation);
        switch (candidate.Status)
        {
            case PushdownStatus.Disjoint:
                return new VirtualFilterTablePlan(enforcement, VirtualFilterTier.EmptyKeySet, skipUpstream: true, candidate.Reason);
            case PushdownStatus.Pushable:
                var args = new Dictionary<string, object?> { [candidate.ParameterName!] = candidate.Values!.ToList() };
                VirtualFilterPathLookup? pathLookup = null;
                if (candidate.PathKeyField != null)
                {
                    int keyPair = pushPairs.FindIndex(p => p.Columns.Length == 1 && string.Equals(p.Columns[0], candidate.PathKeyField, StringComparison.OrdinalIgnoreCase));
                    pathLookup = new VirtualFilterPathLookup(candidate.PathKeyField, table.Columns[keyPair][0], table.KeyTypes[keyPair][0], candidate.Values!);
                }

                return new VirtualFilterTablePlan(
                    enforcement, isPointLookup ? VirtualFilterTier.PointLookup : VirtualFilterTier.Pushdown, skipUpstream: false, candidate.Reason, args, pathLookup);
            default:
                return needsPushdown
                    ? throw PushdownDenied(table, candidate.Status == PushdownStatus.OverLimit
                        ? VirtualFilterErrorCodes.PushdownLimitExceeded
                        : VirtualFilterErrorCodes.PushdownUnavailable)
                    : new VirtualFilterTablePlan(enforcement, VirtualFilterTier.FullFetch, skipUpstream: false, candidate.Reason);
        }
    }

    private static GatewaySecurityException PushdownDenied(PreparedTable table, string code) =>
        new($"The Virtual Filter bindings of '{table.Metadata.Identifier}' require a key pushdown that is not available within its limits.", code);

    /// <summary>Per pair: the queried key tuples of the point lookup, or null when the pair is not decided by the query.</summary>
    private static VirtualFilterKey[]?[]? QueriedKeys(
        SqlStatement? statement, IReadOnlyList<ResolvedSourceTable> tables, PreparedTable table, int maxTuples)
    {
        var lookup = VirtualFilterPointLookupExtractor.Extract(statement, tables, table.Metadata.Identifier);
        if (lookup == null || lookup.Count == 0)
        {
            return null;
        }

        var result = new VirtualFilterKey[]?[table.Pairs.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = VirtualFilterPointLookupKeys.TryBuild(lookup, table.Columns[i], table.KeyTypes[i], maxTuples);
        }

        return result;
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
        VirtualFilterKeyType[][] KeyTypes,
        string[][] Columns);
}
