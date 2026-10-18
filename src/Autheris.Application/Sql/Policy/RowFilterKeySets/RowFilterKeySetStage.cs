namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Diagnostics;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-2, A-10, A-13, D-WPC-2, -3, -7, -9): the per-statement stage that replaces eligible correlated <c>EXISTS</c> sites by
/// cached key sets. It runs after admission, rewrites the compiler input only (never a decision, recipe or cache: SEC-RF107-04 a, -10 c) and
/// fails safe: every condition that is not met leaves the exact <c>EXISTS</c>.
/// <list type="bullet">
/// <item>Per site: resolve settings (rule, object, global), data source opt-in (SEC-RF107-01 c), dialect, authoritative epochs (read once before the
/// lookup, SEC-RF107-04 e, -05), key query compile (only <see cref="KeyQueryNotDerivableException"/> maps to a fallback, SEC-RF107-08), the
/// pre-load text gate (D-WPC-3), the single-flight cache and the loader (a loader failure is a <c>ROW_FILTER_KEY_SET_UNAVAILABLE</c> denial).</item>
/// <item>After all sites are resolved one combined loop applies the bind budget and the text budget; a budget fallback never touches the entry (SEC-RF107-18).</item>
/// <item>Site ordinals are unique per statement and assigned before any fallback decision (D-WPC-7). Errors of the substitution are programming errors and propagate.</item>
/// <item>No key value, no hash of key values and no count reaches a log line here; counts go to the report only (SEC-RF107-09).</item>
/// </list>
/// </summary>
public sealed class RowFilterKeySetStage : IRowFilterKeySetStage
{
    private const int WarnedCap = 4_096;

    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly IPolicyKeyQueryCompiler _compiler;
    private readonly IEpochValidationService _epochs;
    private readonly RowFilterKeySetCache _cache;
    private readonly IRowFilterKeySetLoader _loader;
    private readonly ILogger<RowFilterKeySetStage>? _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    public RowFilterKeySetStage(
        IOptionsMonitor<GatewayOptions> options,
        IPolicyKeyQueryCompiler compiler,
        IEpochValidationService epochs,
        RowFilterKeySetCache cache,
        IRowFilterKeySetLoader loader,
        ILogger<RowFilterKeySetStage>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _epochs = epochs ?? throw new ArgumentNullException(nameof(epochs));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _logger = logger;
    }

    /// <summary>Test seam of D-WPC-7: the substitution; production is <see cref="CorrelatedKeySetSubstitution.Substitute"/>.</summary>
    internal Func<PolicyPredicate, IReadOnlyList<(ExistsExpression Node, KeySetReplacement Replacement)>, TargetSqlDialect, PolicyPredicate> Substituter { get; init; }
        = CorrelatedKeySetSubstitution.Substitute;

    public void ReportSkipped(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        KeySetFallbackReason reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(report);
        var global = _options.CurrentValue.RowFilters.KeySetCache;
        int before = report.Outcomes.Count;
        foreach (var site in Scan(context, predicates, global, warn: false))
        {
            if (site.Eval.Strategy == RowFilterEvaluationStrategy.KeySetCache)
            {
                report.Add(Outcome(site, KeySetOutcomeKind.Fallback, reason));
            }
        }

        RecordMetrics(context.Dialect, report, before);
    }

    public async ValueTask<IReadOnlyDictionary<TableIdentity, PolicyPredicate?>> ApplyAsync(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(predicates);
        ArgumentNullException.ThrowIfNull(report);

        // SEC-RF107-15 / SEC-RF107-17: the retry and every non-read statement perform no epoch read, cache lookup or key query.
        if (context.Disabled || !context.IsRead)
        {
            return predicates;
        }

        var options = _options.CurrentValue;   // read per statement: lowered caps apply on the next request (SEC-RF107-16)
        var global = options.RowFilters.KeySetCache;
        int firstOutcome = report.Outcomes.Count;
        var sites = Scan(context, predicates, global, warn: true);

        var candidates = new List<Site>();
        foreach (var site in sites)
        {
            if (site.Eval.Strategy != RowFilterEvaluationStrategy.KeySetCache)
            {
                if (site.InvalidStored)
                {
                    report.Add(Outcome(site, KeySetOutcomeKind.Fallback, KeySetFallbackReason.InvalidStoredSetting));
                }

                continue;
            }

            if (!site.Substitutable)
            {
                report.Add(Outcome(site, KeySetOutcomeKind.Fallback, site.Excluded ? (site.Shared ? KeySetFallbackReason.Deny_Shared : KeySetFallbackReason.Deny_Negated) : KeySetFallbackReason.Temporal));
                continue;
            }

            candidates.Add(site);
        }

        if (candidates.Count == 0)
        {
            RecordMetrics(context.Dialect, report, firstOutcome);
            return predicates;
        }

        var result = await ResolveAsync(context, predicates, report, candidates, options, global, ct).ConfigureAwait(false);
        RecordMetrics(context.Dialect, report, firstOutcome);
        return result;
    }

    private async ValueTask<IReadOnlyDictionary<TableIdentity, PolicyPredicate?>> ResolveAsync(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        List<Site> candidates,
        GatewayOptions options,
        RowFilterKeySetCacheOptions global,
        CancellationToken ct)
    {
        // SEC-RF107-01 c / dialect: the same for every site of the statement.
        if (context.Dialect is not (TargetSqlDialect.SqlServer or TargetSqlDialect.PostgreSql or TargetSqlDialect.Oracle or TargetSqlDialect.Sqlite))
        {
            FallbackAll(report, candidates, KeySetFallbackReason.DialectUnsupported);
            return predicates;
        }

        if (options.DataSources?.Connections is not { } connections
            || !connections.TryGetValue(context.DataSourceName, out var connection)
            || !connection.KeySetCacheAllowed
            || !DataSourceProvider.TryResolveDialect(connection.Provider, out var connectionDialect)
            || connectionDialect != ToDatabaseDialect(context.Dialect))
        {
            FallbackAll(report, candidates, KeySetFallbackReason.SourceNotEligible);
            return predicates;
        }

        // Key query compilation and converter derivation need no epoch: sites that cannot be substituted never cause an authoritative read (SEC-RF107-04 e).
        var prepared = new List<Prepared>();
        foreach (var site in candidates.OrderBy(static c => c.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var item = Prepare(context, predicates, site, ct);
            if (item.Reason is { } reason)
            {
                report.Add(Outcome(site, KeySetOutcomeKind.Fallback, reason));
                continue;
            }

            prepared.Add(item.Prepared!);
        }

        if (prepared.Count == 0)
        {
            return predicates;
        }

        // SEC-RF107-04 e / -05: target and dependent epochs are read once, before any lookup; the same values go into the keys.
        var epochTables = new HashSet<TableIdentifier>();
        foreach (var table in prepared.Select(static c => c.Site.Table).Distinct())
        {
            if (context.GovernanceIdentifiers.TryGetValue(table, out var ids))
            {
                foreach (var id in ids) epochTables.Add(id);
            }
        }

        var epochs = await _epochs.TryGetAuthoritativeEpochsAsync(epochTables, ct).ConfigureAwait(false);
        if (epochs is null)
        {
            FallbackAll(report, prepared.Select(static p => p.Site), KeySetFallbackReason.EpochUnavailable);
            return predicates;
        }

        long keyListBudget = RowFilterKeySetTextBudget.KeyListBudget(context.UserSqlLength, context.MaxExpansionFactor);
        long gateUsed = 0;
        var loaded = new List<Loaded>();
        foreach (var item in prepared)
        {
            ct.ThrowIfCancellationRequested();
            var result = await TryLoadAsync(context, item, connection, global, epochs, keyListBudget, gateUsed, ct).ConfigureAwait(false);
            if (result.Reason is { } reason)
            {
                report.Add(Outcome(item.Site, KeySetOutcomeKind.Fallback, reason));
                continue;
            }

            gateUsed += RowFilterKeySetTextBudget.EstimateListChars(context.Dialect, item.Site.Ordinal, 1, result.Loaded!.ForeignKeyColumn);
            loaded.Add(result.Loaded!);
        }

        if (loaded.Count == 0)
        {
            return predicates;
        }

        DropBudgetExceeding(context, predicates, report, loaded, global, keyListBudget);
        if (loaded.Count == 0)
        {
            return predicates;
        }

        var rewritten = new Dictionary<TableIdentity, PolicyPredicate?>(predicates);
        foreach (var group in loaded.GroupBy(static l => l.Site.Table))
        {
            var replacements = group.OrderBy(static l => l.Site.Ordinal)
                .Select(static l => (l.Site.Node!, l.Replacement))
                .ToList();
            // D-WPC-7: InvalidOperationException, ArgumentException and PolicyConflictException are programming errors; no catch here.
            rewritten[group.Key] = Substituter(predicates[group.Key]!, replacements, context.Dialect);
            foreach (var l in group.OrderBy(static l => l.Site.Ordinal))
            {
                report.Add(Outcome(l.Site, l.Hit ? KeySetOutcomeKind.Hit : KeySetOutcomeKind.Miss, null) with
                {
                    KeyCount = l.Replacement.Keys.Count,
                    PaddedCount = l.Replacement.PaddedCount,
                    AgeSeconds = l.AgeSeconds
                });
            }
        }

        return rewritten;
    }

    private (Prepared? Prepared, KeySetFallbackReason? Reason) Prepare(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        Site site,
        CancellationToken ct)
    {
        var target = context.Catalog.Resolve(site.Table.ToQualifiedName());
        if (target is null)
        {
            return (null, KeySetFallbackReason.NotDerivable);
        }

        CompiledKeyQuery query;
        try
        {
            query = _compiler.CompileKeyQuery(
                new PolicyKeyQueryRequest(site.Node!, predicates[site.Table]!.Parameters, context.Catalog, target, context.TenantBinding, context.Dialect, site.Eval.MaxKeys + 1), ct);
        }
        catch (KeyQueryNotDerivableException ex)
        {
            // SEC-RF107-08: the only exception that maps to a fallback; SecurityException and SqlLimitExceededException propagate.
            return (null, Enum.TryParse<KeySetFallbackReason>(ex.ReasonCode, out var mapped)
                          && mapped is KeySetFallbackReason.NotDerivable or KeySetFallbackReason.TypeNotBindable or KeySetFallbackReason.DialectUnsupported
                ? mapped
                : KeySetFallbackReason.NotDerivable);
        }

        var converter = KeySetValueConverter.Create(context.Dialect, query.KeyColumnType, query.ForeignKeyColumnType);
        return converter is null
            ? (null, KeySetFallbackReason.TypeNotBindable)
            : (new Prepared(site, query, converter), null);
    }

    private async ValueTask<(Loaded? Loaded, KeySetFallbackReason? Reason)> TryLoadAsync(
        RowFilterKeySetContext context,
        Prepared prepared,
        DataSourceConnectionOptions connection,
        RowFilterKeySetCacheOptions global,
        IReadOnlyDictionary<TableIdentifier, long> epochs,
        long keyListBudget,
        long gateUsed,
        CancellationToken ct)
    {
        var (site, query, converter) = prepared;

        // D-WPC-3 pre-load gate: a site whose 1-key list does not fit the remaining text budget never loads.
        if (gateUsed + RowFilterKeySetTextBudget.EstimateListChars(context.Dialect, site.Ordinal, 1, query.ForeignKeyColumn) > keyListBudget)
        {
            return (null, KeySetFallbackReason.ExpansionBudget);
        }

        var governance = context.GovernanceIdentifiers.TryGetValue(site.Table, out var ids) ? ids : [];
        var subset = new Dictionary<TableIdentifier, long>();
        foreach (var id in governance)
        {
            subset[id] = epochs[id];
        }

        string tenant = context.Tenant.Value;
        string cacheKey = RowFilterKeySetCacheKey.Compute(new RowFilterKeySetCacheKeyInput(
            connection.Provider, connection.ConnectionString, context.Dialect, tenant, context.DataSourceName, query, site.Eval.MaxKeys, subset));
        var timeout = global.KeyQueryTimeout;
        var request = new RowFilterKeySetLoadRequest(connection, context.Dialect, context.Tenant, context.DataSourceName, query, converter, site.Eval.MaxKeys, timeout);

        // SEC-RF107-08: a loader failure (GatewaySecurityException ROW_FILTER_KEY_SET_UNAVAILABLE) propagates and denies the statement.
        var result = await _cache.GetOrLoadAsync(
            cacheKey, tenant, context.DataSourceName, site.Eval.Duration,
            async loadCt =>
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    return await _loader.LoadAsync(request, loadCt).ConfigureAwait(false);
                }
                finally
                {
                    GatewayDiagnostics.RowFilterKeyQueryDuration.Record(
                        Stopwatch.GetElapsedTime(start).TotalMilliseconds, new KeyValuePair<string, object?>("dialect", context.Dialect.ToString()));
                }
            },
            timeout + TimeSpan.FromSeconds(1),
            ct).ConfigureAwait(false);

        if (result.Status == RowFilterKeySetCacheStatus.Throttled || result.Entry is null)
        {
            return (null, KeySetFallbackReason.LoadThrottled);
        }

        var entry = result.Entry;
        if (entry.Overflow)
        {
            return (null, KeySetFallbackReason.Overflow);
        }

        if (entry.NotBindable)
        {
            return (null, KeySetFallbackReason.ValueNotBindable);
        }

        var values = new List<PolicyValue>(entry.Keys.Count);
        foreach (object key in entry.Keys)
        {
            values.Add(new PolicyValue(key, converter.ParameterType));
        }

        int padded = KeySetPadding.Bucket(values.Count, site.Eval.MaxKeys);
        var replacement = new KeySetReplacement(query.ForeignKeyColumn, values, padded, site.Ordinal);
        long estimate = RowFilterKeySetTextBudget.EstimateListChars(context.Dialect, site.Ordinal, padded, query.ForeignKeyColumn);
        return (new Loaded(site, replacement, query.ForeignKeyColumn, result.Status == RowFilterKeySetCacheStatus.Hit, result.AgeSeconds, estimate), null);
    }

    /// <summary>
    /// D-WPC-3: one combined loop over the bind budget (A-10) and the text budget. While either is exceeded the site with the largest padded list
    /// falls back (lowest ordinal on ties): <c>ParameterBudget</c> when the bind budget is exceeded at that moment, else <c>ExpansionBudget</c>.
    /// Nothing is cached or evicted here (SEC-RF107-18).
    /// </summary>
    private static void DropBudgetExceeding(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetReport report,
        List<Loaded> loaded,
        RowFilterKeySetCacheOptions global,
        long keyListBudget)
    {
        int maxBinds = DialectCapabilityTable.Default.TryGet(context.Dialect, out var caps) ? caps.MaxBindParameters : 1000;
        long existing = predicates.Values.Sum(static p => p?.Parameters.Count ?? 0);
        long bindBudget = maxBinds - existing - global.BindParameterReserve;

        // Per-site estimates are computed once (Loaded.EstimatedChars); the running sums shrink as sites drop out.
        long binds = 0;
        long chars = 0;
        foreach (var l in loaded)
        {
            binds += l.Replacement.PaddedCount;
            chars += l.EstimatedChars;
        }

        while (loaded.Count > 0)
        {
            bool bindsExceeded = binds > bindBudget;
            if (!bindsExceeded && chars <= keyListBudget)
            {
                return;
            }

            var largest = loaded[0];
            foreach (var l in loaded)
            {
                if (l.Replacement.PaddedCount > largest.Replacement.PaddedCount
                    || (l.Replacement.PaddedCount == largest.Replacement.PaddedCount && l.Site.Ordinal < largest.Site.Ordinal))
                {
                    largest = l;
                }
            }

            if (largest.Replacement.PaddedCount == 0)
            {
                return;   // nothing left that adds binds or text beyond the constant 1 = 0
            }

            loaded.Remove(largest);
            binds -= largest.Replacement.PaddedCount;
            chars -= largest.EstimatedChars;
            report.Add(Outcome(largest.Site, KeySetOutcomeKind.Fallback, bindsExceeded ? KeySetFallbackReason.ParameterBudget : KeySetFallbackReason.ExpansionBudget));
        }
    }

    /// <summary>Every registered site of the statement in D-WPC-7 order (tables by ordinal identity string, sites in pre-order) with resolved settings.</summary>
    private List<Site> Scan(
        RowFilterKeySetContext context,
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates,
        RowFilterKeySetCacheOptions global,
        bool warn)
    {
        var sites = new List<Site>();
        int ordinal = 0;
        foreach (var table in predicates.Keys.OrderBy(static t => t.ToString(), StringComparer.Ordinal))
        {
            if (predicates[table] is not { } predicate)
            {
                continue;
            }

            var objectSettings = context.Metadata.TryGetValue(table, out var meta) ? meta?.RowFilterEvaluation : null;
            var scan = CorrelatedKeySetSubstitution.Scan(predicate);
            foreach (var (node, site) in scan.Eligible)
            {
                bool structural = site.StructurallyEligible;
                sites.Add(Create(table, node, site, objectSettings, global, ordinal++, excluded: false, substitutable: structural, warn, ineligible: !structural));
            }

            foreach (var (node, site) in scan.Excluded)
            {
                sites.Add(Create(table, node, site, objectSettings, global, -1, excluded: true, substitutable: false, warn, ineligible: true, shared: scan.Shared.Contains(node)));
            }
        }

        return sites;
    }

    private Site Create(
        TableIdentity table, ExistsExpression node, CorrelatedFilterSite site, RowFilterEvaluationSettings? objectSettings,
        RowFilterKeySetCacheOptions global, int ordinal, bool excluded, bool substitutable, bool warn, bool ineligible, bool shared = false)
    {
        var eval = RowFilterEvaluationResolver.Resolve(site.RuleSettings, objectSettings, global);
        bool invalid = RowFilterEvaluationResolver.HasInvalidStoredValue(site.RuleSettings) || RowFilterEvaluationResolver.HasInvalidStoredValue(objectSettings);
        if (warn)
        {
            if (invalid)
            {
                WarnOnce($"invalid:{site.RuleId?.ToString() ?? table.ToString()}", "Stored row-filter key-set settings hold an invalid value and are ignored; the rule keeps EXISTS.");
            }

            if (ineligible && site.RuleSettings is not null)
            {
                WarnOnce($"ineligible:{site.RuleId?.ToString() ?? table.ToString()}", "Stored row-filter key-set settings sit on a rule that is not eligible for a key set (negated, shared or temporal) and are ignored.");
            }
        }

        return new Site(table, node, site, eval, ordinal, excluded, substitutable, invalid, shared);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Count >= WarnedCap || !_warned.TryAdd(key, 0))
        {
            return;
        }

        // No values; the rule id (a guid) is the only identifier and is part of the dedupe key, not of the message template data of other sites.
        _logger?.LogWarning("{Message} Key={Key}", message, key[(key.IndexOf(':') + 1)..]);
    }

    private static void FallbackAll(RowFilterKeySetReport report, IEnumerable<Site> candidates, KeySetFallbackReason reason)
    {
        foreach (var site in candidates)
        {
            report.Add(Outcome(site, KeySetOutcomeKind.Fallback, reason));
        }
    }

    private static RowFilterKeySetOutcome Outcome(Site site, KeySetOutcomeKind kind, KeySetFallbackReason? reason) =>
        new(site.Table.ToString(), site.Info.RuleId, site.Info.Source, site.Eval.StrategySource, kind, reason, null, null, null);

    private static void RecordMetrics(TargetSqlDialect dialect, RowFilterKeySetReport report, int fromIndex)
    {
        string d = dialect.ToString();
        for (int i = fromIndex; i < report.Outcomes.Count; i++)
        {
            var o = report.Outcomes[i];
            switch (o.Outcome)
            {
                case KeySetOutcomeKind.Hit:
                    GatewayDiagnostics.RowFilterKeySetHits.Add(1, new("dialect", d), new("outcome", "Hit"));
                    break;
                case KeySetOutcomeKind.Miss:
                    GatewayDiagnostics.RowFilterKeySetMisses.Add(1, new("dialect", d), new("outcome", "Miss"));
                    break;
                case KeySetOutcomeKind.Fallback:
                    GatewayDiagnostics.RowFilterKeySetFallbacks.Add(1, new("dialect", d), new("outcome", "Fallback"), new("reason", o.Reason.ToString()));
                    break;
            }
        }
    }

    private static DatabaseDialect ToDatabaseDialect(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => DatabaseDialect.SqlServer,
        TargetSqlDialect.PostgreSql => DatabaseDialect.PostgreSql,
        TargetSqlDialect.Oracle => DatabaseDialect.Oracle,
        TargetSqlDialect.Sqlite => DatabaseDialect.Sqlite,
        _ => throw new NotSupportedException("The key-set cache has no loader for this dialect.")
    };

    private sealed record Site(
        TableIdentity Table, ExistsExpression? Node, CorrelatedFilterSite Info, EffectiveRowFilterEvaluation Eval,
        int Ordinal, bool Excluded, bool Substitutable, bool InvalidStored, bool Shared);

    private sealed record Loaded(Site Site, KeySetReplacement Replacement, string ForeignKeyColumn, bool Hit, int? AgeSeconds, long EstimatedChars);

    private sealed record Prepared(Site Site, CompiledKeyQuery Query, KeySetValueConverter Converter);
}
