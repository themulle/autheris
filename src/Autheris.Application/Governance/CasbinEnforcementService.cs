namespace Autheris.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Casbin;
using Casbin.Model;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

public sealed record CasbinRuleInput(
    string Sub,
    string Obj,
    string Act,
    string SubRule = "true",
    string Eft = "allow",
    string? RlsFilter = null,
    ConsentRowFilter? CorrelatedRowFilter = null);

public sealed partial class CasbinEnforcementService : IPolicyEnforcementService, IDisposable
{
    internal sealed record GroupingRule(string User, string Role);

    private sealed record PolicySources(
        ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> GlobalRules,
        ImmutableArray<GroupingRule> GlobalGrouping,
        ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> TenantFiles,
        ImmutableDictionary<string, ImmutableArray<GroupingRule>> TenantGrouping,
        ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> ProgrammaticRules,
        ImmutableDictionary<string, ImmutableArray<GroupingRule>> ProgrammaticGrouping)
    {
        public static readonly PolicySources Empty = new(
            ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableArray<GroupingRule>.Empty,
            ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, ImmutableArray<GroupingRule>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary<string, ImmutableArray<GroupingRule>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

        public bool HasAnyPolicies =>
            GlobalRules.Values.Any(r => r.Length > 0) ||
            TenantFiles.Values.Any(r => r.Length > 0) ||
            ProgrammaticRules.Values.Any(r => r.Length > 0);

        public bool TenantHasPolicies(string tenant)
        {
            if (TenantFiles.TryGetValue(tenant, out var tf) && tf.Length > 0) return true;
            if (TenantGrouping.TryGetValue(tenant, out var tg) && tg.Length > 0) return true;
            if (ProgrammaticRules.TryGetValue(tenant, out var pr) && pr.Length > 0) return true;
            if (ProgrammaticGrouping.TryGetValue(tenant, out var pg) && pg.Length > 0) return true;
            if (GlobalRules.TryGetValue(tenant, out var gr) && gr.Length > 0) return true;
            return false;
        }

        public IEnumerable<string> AllTenants =>
            GlobalRules.Keys
                .Concat(TenantFiles.Keys)
                .Concat(TenantGrouping.Keys)
                .Concat(ProgrammaticRules.Keys)
                .Concat(ProgrammaticGrouping.Keys)
                .Where(t => !string.Equals(t, "*", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct CandidateKey(string Tenant, string Table, string Action);

    /// <summary>
    /// The rules of a tenant that can influence a decision for one table and action (tenant, object and action already match), in policy
    /// order, with the cache-shape facts of <see cref="Caching.PolicyShapeAnalyzer"/>.
    /// </summary>
    private sealed record CandidateSet(
        CasbinRuleMetadata[] Rules,
        bool Shareable,
        bool ReadsContext,
        bool ReferencesTime);

    private sealed class PolicySnapshot(
        FrozenDictionary<string, Enforcer> enforcers,
        FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>> rules,
        FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>> roleClosures,
        FrozenDictionary<string, FrozenSet<string>> wildcardRoleClosure,
        Enforcer wildcardEnforcer,
        long epoch)
    {
        public FrozenDictionary<string, Enforcer> Enforcers { get; } = enforcers;
        public FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules { get; } = rules;
        public FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>> RoleClosures { get; } = roleClosures;
        public FrozenDictionary<string, FrozenSet<string>> WildcardRoleClosure { get; } = wildcardRoleClosure;
        public Enforcer WildcardEnforcer { get; } = wildcardEnforcer;
        public long Epoch { get; } = epoch;

        /// <summary>WP-PF-09: candidate rules per (tenant, table, action); a snapshot is immutable, so entries never go stale.</summary>
        public Autheris.Application.Caching.BoundedCache<CandidateKey, CandidateSet> Candidates { get; } = new(20_000);

        public FrozenDictionary<string, FrozenSet<string>> GetRoleClosure(string tenant) =>
            RoleClosures.TryGetValue(tenant, out var closure) ? closure : WildcardRoleClosure;

        public bool HasPolicies(string tenant) =>
            (Rules.TryGetValue(tenant, out var r) && r.Length > 0) ||
            (Rules.TryGetValue(WildcardTenant, out var wr) && wr.Length > 0);
    }

    public const string WildcardTenant = "*";

    private PolicySources _sources = PolicySources.Empty;
    private PolicySnapshot _currentSnapshot;
    private readonly object _syncLock = new();
    private readonly bool _modelSupportsWildcardTenant;

    private readonly CasbinDecisionCache _decisionCache;
    private readonly PolicyEpochGuard? _epochGuard;
    private long _decisionCacheHits;
    private long _decisionEvaluations;
    private readonly bool _shareAcrossUsers;
    private readonly ConcurrentDictionary<string, string> _tenantPolicyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private string? _globalPolicyFilePath;
    private FileSystemWatcher? _globalFileWatcher;
    private System.Timers.Timer? _globalDebounceTimer;
    private readonly IRlsFilterGenerator _rlsFilterGenerator;
    private readonly Autheris.Application.Policy.RowPolicyFactory? _rowPolicies;
    private readonly string _modelText;

    public event Action<string>? OnPolicyReloaded;
    private readonly ILogger<CasbinEnforcementService>? _logger;

    public sealed record CasbinRuleMetadata(
        string Sub,
        string Tenant,
        string Obj,
        string Act,
        string SubRule,
        string Eft,
        string? RlsFilter = null,
        ConsentRowFilter? CorrelatedRowFilter = null);

    public const string DefaultModelText = @"
[request_definition]
r = sub, tenant, obj, act, ctx

[policy_definition]
p = sub, tenant, obj, act, sub_rule, eft

[role_definition]
g = _, _

[policy_effect]
e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

[matchers]
m = g(r.sub, p.sub) && (r.tenant == p.tenant || p.tenant == ""*"") && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == ""*"") && eval(p.sub_rule)
";

    public CasbinEnforcementService(
        string? modelConfigPath = null,
        IRlsFilterGenerator? rlsFilterGenerator = null,
        ILogger<CasbinEnforcementService>? logger = null,
        Autheris.Application.Policy.RowPolicyFactory? rowPolicies = null,
        CasbinDecisionCacheOptions? cacheOptions = null,
        TimeProvider? timeProvider = null,
        IDistributedClusterStateProvider? stateProvider = null,
        IClusterStateTopology? topology = null)
    {
        cacheOptions ??= new CasbinDecisionCacheOptions();
        timeProvider ??= TimeProvider.System;
        _decisionCache = new CasbinDecisionCache(
            cacheOptions.Capacity, TimeSpan.FromSeconds(cacheOptions.TtlSeconds), timeProvider);
        _shareAcrossUsers = cacheOptions.ShareAcrossUsers;
        _rowPolicies = rowPolicies;
        _logger = logger;
        _rlsFilterGenerator = rlsFilterGenerator ?? RlsFilterGenerator.Instance;

        string modelSource;
        if (!string.IsNullOrWhiteSpace(modelConfigPath))
        {
            if (!File.Exists(modelConfigPath))
            {
                throw new FileNotFoundException($"Casbin model config file not found: {modelConfigPath}", modelConfigPath);
            }
            _modelText = File.ReadAllText(modelConfigPath);
            modelSource = $"file '{modelConfigPath}'";
        }
        else
        {
            _modelText = DefaultModelText;
            modelSource = "embedded default";
        }

        var capabilities = CasbinModelContract.Verify(_modelText);
        _modelSupportsWildcardTenant = capabilities.SupportsWildcardTenant;

        _logger?.LogInformation("Casbin model loaded from {ModelSource}. SupportsWildcardTenant: {SupportsWildcardTenant}", modelSource, _modelSupportsWildcardTenant);

        var initialWildcardEnforcer = new Enforcer(DefaultModel.CreateFromText(_modelText));
        var initialWildcardRoleClosure = FrozenDictionary<string, FrozenSet<string>>.Empty;
        _currentSnapshot = new PolicySnapshot(
            FrozenDictionary<string, Enforcer>.Empty,
            FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>>.Empty,
            FrozenDictionary<string, FrozenDictionary<string, FrozenSet<string>>>.Empty,
            initialWildcardRoleClosure,
            initialWildcardEnforcer,
            1);

        if (topology?.IsClusterWide == true)
        {
            // Cluster-wide state: policy changes propagate through a shared epoch (throws when no state provider is available).
            _epochGuard = new PolicyEpochGuard(
                stateProvider,
                timeProvider,
                TimeSpan.FromSeconds(cacheOptions.EpochCheckSeconds),
                TimeSpan.FromMilliseconds(cacheOptions.UnavailableBackoffMilliseconds),
                logger,
                idleEvictionAfter: TimeSpan.FromSeconds(cacheOptions.TtlSeconds));
        }
    }

    public long CurrentEpoch => Volatile.Read(ref _currentSnapshot).Epoch;
    public string ModelText => _modelText;
    public bool ModelSupportsWildcardTenant => _modelSupportsWildcardTenant;
    public bool HasPolicies(TenantId tenant) => Volatile.Read(ref _currentSnapshot).HasPolicies(tenant.Value);

    internal Enforcer GetEnforcer(TenantId tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        return snapshot.Enforcers.TryGetValue(tenant.Value, out var existing) ? existing : snapshot.WildcardEnforcer;
    }

    private Enforcer CreateEnforcer(
        IEnumerable<CasbinRuleMetadata> ownRules,
        IEnumerable<CasbinRuleMetadata> wildcardRules,
        IEnumerable<GroupingRule> groupingRules)
    {
        var model = DefaultModel.CreateFromText(_modelText);
        var enforcer = new Enforcer(model);

        foreach (var rule in ownRules)
        {
            var normalizedSubRule = Regex.Replace(rule.SubRule, @"'([^']{2,})'", "\"$1\"");
            enforcer.AddPolicy(rule.Sub, rule.Tenant, rule.Obj, rule.Act, normalizedSubRule, rule.Eft);
        }

        foreach (var rule in wildcardRules)
        {
            var normalizedSubRule = Regex.Replace(rule.SubRule, @"'([^']{2,})'", "\"$1\"");
            enforcer.AddPolicy(rule.Sub, rule.Tenant, rule.Obj, rule.Act, normalizedSubRule, rule.Eft);
        }

        foreach (var g in groupingRules)
        {
            enforcer.AddGroupingPolicy(g.User, g.Role);
        }

        return enforcer;
    }

    private PolicySnapshot BuildSnapshot(
        PolicySources src,
        long epoch,
        PolicySnapshot? previousSnapshot = null,
        string? changedTenant = null)
    {
        var wildcardRules = new List<CasbinRuleMetadata>();
        if (src.GlobalRules.TryGetValue("*", out var gw)) wildcardRules.AddRange(gw);
        if (src.ProgrammaticRules.TryGetValue("*", out var pw)) wildcardRules.AddRange(pw);

        var wildcardGrouping = new List<GroupingRule>(src.GlobalGrouping);
        if (src.ProgrammaticGrouping.TryGetValue("*", out var pwg)) wildcardGrouping.AddRange(pwg);

        var enforcersDict = new Dictionary<string, Enforcer>(StringComparer.OrdinalIgnoreCase);
        var rulesDict = new Dictionary<string, ImmutableArray<CasbinRuleMetadata>>(StringComparer.OrdinalIgnoreCase);
        var roleClosuresDict = new Dictionary<string, FrozenDictionary<string, FrozenSet<string>>>(StringComparer.OrdinalIgnoreCase);

        // F-8 optimization: reuse wildcard enforcer when only a single non-wildcard tenant changed
        bool isSingleTenantChange = !string.IsNullOrEmpty(changedTenant) && changedTenant != "*";
        Enforcer wildcardEnforcer;
        FrozenDictionary<string, FrozenSet<string>> wildcardRoleClosure;
        if (isSingleTenantChange && previousSnapshot != null)
        {
            wildcardEnforcer = previousSnapshot.WildcardEnforcer;
            wildcardRoleClosure = previousSnapshot.WildcardRoleClosure;
            rulesDict["*"] = previousSnapshot.Rules.TryGetValue("*", out var existingW)
                ? existingW
                : wildcardRules.ToImmutableArray();
            roleClosuresDict["*"] = wildcardRoleClosure;
        }
        else
        {
            wildcardEnforcer = CreateEnforcer(Enumerable.Empty<CasbinRuleMetadata>(), wildcardRules, wildcardGrouping);
            rulesDict["*"] = wildcardRules.ToImmutableArray();
            wildcardRoleClosure = BuildRoleClosure(wildcardGrouping);
            roleClosuresDict["*"] = wildcardRoleClosure;
        }

        foreach (var tenant in src.AllTenants)
        {
            // F-8 optimization: if only a specific tenant changed, reuse other tenants' enforcers
            if (isSingleTenantChange &&
                previousSnapshot != null &&
                !tenant.Equals(changedTenant, StringComparison.OrdinalIgnoreCase) &&
                previousSnapshot.Enforcers.TryGetValue(tenant, out var existingTenantEnforcer) &&
                previousSnapshot.Rules.TryGetValue(tenant, out var existingTenantRules) &&
                previousSnapshot.RoleClosures.TryGetValue(tenant, out var existingTenantClosure))
            {
                enforcersDict[tenant] = existingTenantEnforcer;
                rulesDict[tenant] = existingTenantRules;
                roleClosuresDict[tenant] = existingTenantClosure;
                continue;
            }

            var tenantRules = new List<CasbinRuleMetadata>();
            if (src.GlobalRules.TryGetValue(tenant, out var gr)) tenantRules.AddRange(gr);
            if (src.TenantFiles.TryGetValue(tenant, out var tr)) tenantRules.AddRange(tr);
            if (src.ProgrammaticRules.TryGetValue(tenant, out var pr)) tenantRules.AddRange(pr);

            var tenantGrouping = new List<GroupingRule>(wildcardGrouping);
            if (src.TenantGrouping.TryGetValue(tenant, out var tg)) tenantGrouping.AddRange(tg);
            if (src.ProgrammaticGrouping.TryGetValue(tenant, out var pg)) tenantGrouping.AddRange(pg);

            enforcersDict[tenant] = CreateEnforcer(tenantRules, wildcardRules, tenantGrouping);
            rulesDict[tenant] = tenantRules.ToImmutableArray();
            roleClosuresDict[tenant] = BuildRoleClosure(tenantGrouping);
        }

        return new PolicySnapshot(
            enforcersDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            rulesDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            roleClosuresDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            wildcardRoleClosure,
            wildcardEnforcer,
            epoch);
    }

    private static FrozenDictionary<string, FrozenSet<string>> BuildRoleClosure(IEnumerable<GroupingRule> groupingRules)
    {
        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groupingRules)
        {
            if (!graph.TryGetValue(g.User, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                graph[g.User] = set;
            }
            set.Add(g.Role);
        }

        var closure = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in graph.Keys)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(user);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (graph.TryGetValue(current, out var directRoles))
                {
                    foreach (var role in directRoles)
                    {
                        if (visited.Add(role)) queue.Enqueue(role);
                    }
                }
            }

            closure[user] = visited.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        }

        return closure.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private void Publish(
        Func<PolicySources, PolicySources> change,
        Action<PolicySources, PolicySources>? validate = null,
        string? changedTenant = null,
        bool propagate = true)
    {
        List<string> tenantsToNotify;
        lock (_syncLock)
        {
            var nextSources = change(_sources);
            validate?.Invoke(_sources, nextSources);
            var current = Volatile.Read(ref _currentSnapshot);
            var nextSnapshot = BuildSnapshot(nextSources, current.Epoch + 1, current, changedTenant);
            _sources = nextSources;
            Interlocked.Exchange(ref _currentSnapshot, nextSnapshot);
            _decisionCache.Clear();

            if (!string.IsNullOrEmpty(changedTenant) && changedTenant != "*")
            {
                tenantsToNotify = [changedTenant];
            }
            else
            {
                tenantsToNotify = current.Enforcers.Keys
                    .Union(current.Rules.Keys, StringComparer.OrdinalIgnoreCase)
                    .Union(nextSnapshot.Enforcers.Keys, StringComparer.OrdinalIgnoreCase)
                    .Union(nextSnapshot.Rules.Keys, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (propagate && _epochGuard is not null)
        {
            // Fire and forget by design: the guard logs and counts its own failures and never throws.
            _ = _epochGuard.PropagateAsync(string.IsNullOrEmpty(changedTenant) ? PolicyEpochGuard.WildcardTenant : changedTenant);
        }

        foreach (var tName in tenantsToNotify)
        {
            OnPolicyReloaded?.Invoke(tName);
        }
    }

    internal int DiagnosticPolicyCount(string tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        return snapshot.Enforcers.TryGetValue(tenant, out var e)
            ? e.GetPolicy().Count()
            : snapshot.WildcardEnforcer.GetPolicy().Count();
    }

    internal object DiagnosticEnforcerIdentity(string tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        return snapshot.Enforcers.TryGetValue(tenant, out var e) ? e : snapshot.WildcardEnforcer;
    }

    internal static int DiagnosticEnforcerPolicyCount(object enforcer) =>
        enforcer is Enforcer e ? e.GetPolicy().Count() : 0;

    internal int DiagnosticDecisionCacheCount => _decisionCache.Count;
    internal long DiagnosticDecisionCacheHits => Interlocked.Read(ref _decisionCacheHits);
    internal long DiagnosticDecisionEvaluations => Interlocked.Read(ref _decisionEvaluations);

    public void Dispose()
    {
        _epochGuard?.Dispose();
        _globalDebounceTimer?.Dispose();
        _globalFileWatcher?.Dispose();

        foreach (var timer in _debounceTimers.Values) timer.Dispose();
        _debounceTimers.Clear();

        foreach (var watcher in _fileWatchers.Values) watcher.Dispose();
        _fileWatchers.Clear();
    }
}
