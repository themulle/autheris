namespace Autheris.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Casbin;
using Casbin.Model;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class CasbinEnforcementService : IPolicyEnforcementService, IDisposable
{
    private sealed record GroupingRule(string User, string Role);

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

    private sealed class PolicySnapshot
    {
        public FrozenDictionary<string, Enforcer> Enforcers { get; }
        public FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules { get; }
        public Enforcer WildcardEnforcer { get; }
        public long Epoch { get; }

        public PolicySnapshot(
            FrozenDictionary<string, Enforcer> enforcers,
            FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>> rules,
            Enforcer wildcardEnforcer,
            long epoch)
        {
            Enforcers = enforcers;
            Rules = rules;
            WildcardEnforcer = wildcardEnforcer;
            Epoch = epoch;
        }

        public bool HasPolicies(string tenant)
        {
            if (Rules.TryGetValue(tenant, out var r) && r.Length > 0) return true;
            if (Rules.TryGetValue(WildcardTenant, out var wr) && wr.Length > 0) return true;
            return false;
        }
    }

    public const string WildcardTenant = "*";

    private PolicySources _sources = PolicySources.Empty;
    private PolicySnapshot _currentSnapshot;
    private readonly object _syncLock = new();
    private readonly bool _modelSupportsWildcardTenant;

    private sealed record CachedDecision(TableAccessDecision Decision, long CreatedTimestampTicks);
    private readonly ConcurrentDictionary<string, CachedDecision> _decisionCache = new(StringComparer.Ordinal);
    private static readonly TimeSpan DecisionCacheTtl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, string> _tenantPolicyFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private string? _globalPolicyFilePath;
    private FileSystemWatcher? _globalFileWatcher;
    private System.Timers.Timer? _globalDebounceTimer;
    private readonly IRlsFilterGenerator _rlsFilterGenerator;
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
        ConsentRowFilter? CorrelatedRowFilter = null
    );

    internal const string DefaultModelText = @"
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
        ILogger<CasbinEnforcementService>? logger = null)
    {
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
        _currentSnapshot = new PolicySnapshot(
            FrozenDictionary<string, Enforcer>.Empty,
            FrozenDictionary<string, ImmutableArray<CasbinRuleMetadata>>.Empty,
            initialWildcardEnforcer,
            1);
    }

    public long CurrentEpoch => Volatile.Read(ref _currentSnapshot).Epoch;

    public bool HasPolicies(TenantId tenant)
    {
        return Volatile.Read(ref _currentSnapshot).HasPolicies(tenant.Value);
    }

    public Enforcer GetOrCreateEnforcer(TenantId tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot.Enforcers.TryGetValue(tenant.Value, out var existing))
        {
            return existing;
        }

        return snapshot.WildcardEnforcer;
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

    private PolicySnapshot BuildSnapshot(PolicySources src, long epoch)
    {
        var wildcardRules = new List<CasbinRuleMetadata>();
        if (src.GlobalRules.TryGetValue("*", out var gw))
        {
            wildcardRules.AddRange(gw);
        }
        if (src.ProgrammaticRules.TryGetValue("*", out var pw))
        {
            wildcardRules.AddRange(pw);
        }

        var wildcardGrouping = new List<GroupingRule>(src.GlobalGrouping);
        if (src.ProgrammaticGrouping.TryGetValue("*", out var pwg))
        {
            wildcardGrouping.AddRange(pwg);
        }

        var enforcersDict = new Dictionary<string, Enforcer>(StringComparer.OrdinalIgnoreCase);
        var rulesDict = new Dictionary<string, ImmutableArray<CasbinRuleMetadata>>(StringComparer.OrdinalIgnoreCase);

        var wildcardEnforcer = CreateEnforcer(
            Enumerable.Empty<CasbinRuleMetadata>(),
            wildcardRules,
            wildcardGrouping);

        rulesDict["*"] = wildcardRules.ToImmutableArray();

        foreach (var tenant in src.AllTenants)
        {
            var tenantRules = new List<CasbinRuleMetadata>();
            if (src.GlobalRules.TryGetValue(tenant, out var gr))
            {
                tenantRules.AddRange(gr);
            }
            if (src.TenantFiles.TryGetValue(tenant, out var tr))
            {
                tenantRules.AddRange(tr);
            }
            if (src.ProgrammaticRules.TryGetValue(tenant, out var pr))
            {
                tenantRules.AddRange(pr);
            }

            var tenantGrouping = new List<GroupingRule>(wildcardGrouping);
            if (src.TenantGrouping.TryGetValue(tenant, out var tg))
            {
                tenantGrouping.AddRange(tg);
            }
            if (src.ProgrammaticGrouping.TryGetValue(tenant, out var pg))
            {
                tenantGrouping.AddRange(pg);
            }

            var enforcer = CreateEnforcer(tenantRules, wildcardRules, tenantGrouping);
            enforcersDict[tenant] = enforcer;
            rulesDict[tenant] = tenantRules.ToImmutableArray();
        }

        return new PolicySnapshot(
            enforcersDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            rulesDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            wildcardEnforcer,
            epoch);
    }

    private void Publish(
        Func<PolicySources, PolicySources> change,
        Action<PolicySources, PolicySources>? validate = null)
    {
        List<string> tenantsToNotify;
        lock (_syncLock)
        {
            var nextSources = change(_sources);
            validate?.Invoke(_sources, nextSources);
            var current = Volatile.Read(ref _currentSnapshot);
            var nextSnapshot = BuildSnapshot(nextSources, current.Epoch + 1);
            _sources = nextSources;
            Interlocked.Exchange(ref _currentSnapshot, nextSnapshot);
            _decisionCache.Clear();

            tenantsToNotify = current.Enforcers.Keys
                .Union(current.Rules.Keys, StringComparer.OrdinalIgnoreCase)
                .Union(nextSnapshot.Enforcers.Keys, StringComparer.OrdinalIgnoreCase)
                .Union(nextSnapshot.Rules.Keys, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var tName in tenantsToNotify)
        {
            OnPolicyReloaded?.Invoke(tName);
        }
    }

    private static readonly string[] DangerousSubRuleTokens =
    [
        "System.", "System;", "Process", "File.", "Directory.", "Assembly", "GetType", "Activator",
        "Environment.", "AppDomain", "MethodInfo", "Invoke", "Type.", "TypeName", "Reflection",
        "DllImport", "Marshal", "Socket", "WebClient", "HttpClient", "Net.", "Unsafe", "Pointer",
        "Diagnostics.", "Compiler", "IO.", "Security.", "Microsoft.", "Configuration", "Registry"
    ];

    private static readonly Regex SafeSubRulePattern = new(
        @"^[a-zA-Z0-9_.\s()|&!=<>',\[\]""+\-/*]+$",
        RegexOptions.Compiled);

    private static readonly Regex SafeRlsFilterPattern = new(
        @"^[a-zA-Z0-9_.\s()=<>!,'""$+\-*/%:@{}]+$",
        RegexOptions.Compiled);

    private static readonly string[] DangerousSqlTokens =
    [
        ";", "--", "/*", "*/", "@@",
        "DROP ", "ALTER ", "TRUNCATE ", "DELETE ", "INSERT ", "UPDATE ", "EXEC ", "EXECUTE ",
        "UNION ", "INTO ", "INFORMATION_SCHEMA", "XP_", "SP_"
    ];

    private static void ValidateSubRuleTokens(string subRule, string? rlsFilter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subRule);

        if (subRule.Length > 500)
        {
            throw new ArgumentException("Sicherheitsfehler: Casbin sub_rule überschreitet die maximale Länge von 500 Zeichen.", nameof(subRule));
        }

        if (!SafeSubRulePattern.IsMatch(subRule))
        {
            throw new ArgumentException("Sicherheitsfehler: Casbin sub_rule enthält nicht erlaubte Zeichen.", nameof(subRule));
        }

        if (!string.IsNullOrWhiteSpace(rlsFilter))
        {
            if (rlsFilter.Length > 1000)
            {
                throw new ArgumentException("Sicherheitsfehler: Casbin rls_filter überschreitet die maximale Länge von 1000 Zeichen.", nameof(rlsFilter));
            }

            if (!SafeRlsFilterPattern.IsMatch(rlsFilter))
            {
                throw new ArgumentException("Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubte Zeichen.", nameof(rlsFilter));
            }

            foreach (var sqlToken in DangerousSqlTokens)
            {
                if (rlsFilter.Contains(sqlToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubten SQL-Ausdruck '{sqlToken.Trim()}'.", nameof(rlsFilter));
                }
            }
        }

        foreach (var token in DangerousSubRuleTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Sicherheitsfehler: Casbin sub_rule enthält nicht erlaubten Ausdruck '{token}'.", nameof(subRule));
            }

            if (!string.IsNullOrWhiteSpace(rlsFilter) && rlsFilter.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Sicherheitsfehler: Casbin rls_filter enthält nicht erlaubten Ausdruck '{token}'.", nameof(rlsFilter));
            }
        }
    }

    public void AddPolicy(
        TenantId tenant,
        string sub,
        string obj,
        string act,
        string subRule = "true",
        string eft = "allow",
        string? rlsFilter = null,
        ConsentRowFilter? correlatedRowFilter = null)
    {
        ValidateSubRuleTokens(subRule, rlsFilter);

        if (tenant.Value == "*" && !_modelSupportsWildcardTenant)
        {
            throw new InvalidOperationException("Das Casbin-Modell unterstützt keine Wildcard-Mandanten (Probe W1). `*`-Regeln sind nicht erlaubt.");
        }

        var ruleMeta = new CasbinRuleMetadata(sub, tenant.Value, obj, act, subRule, eft, rlsFilter, correlatedRowFilter);

        Publish(src =>
        {
            var currentList = src.ProgrammaticRules.TryGetValue(tenant.Value, out var list)
                ? list
                : ImmutableArray<CasbinRuleMetadata>.Empty;
            var nextList = currentList.Add(ruleMeta);
            var nextDict = src.ProgrammaticRules.SetItem(tenant.Value, nextList);
            return src with { ProgrammaticRules = nextDict };
        });
    }

    public void AddRlsPolicy(
        TenantId tenant,
        string sub,
        string obj,
        string rlsFilter,
        string act = "read",
        string subRule = "true")
    {
        AddPolicy(tenant, sub, obj, act, subRule, "allow", rlsFilter: rlsFilter);
    }

    public void AddRoleForUser(TenantId tenant, string user, string role)
    {
        var grouping = new GroupingRule(user, role);

        Publish(src =>
        {
            var currentList = src.ProgrammaticGrouping.TryGetValue(tenant.Value, out var list)
                ? list
                : ImmutableArray<GroupingRule>.Empty;
            var nextList = currentList.Add(grouping);
            var nextDict = src.ProgrammaticGrouping.SetItem(tenant.Value, nextList);
            return src with { ProgrammaticGrouping = nextDict };
        });
    }

    public void AddWildcardPolicy(
        string sub,
        string obj,
        string act,
        string subRule = "true",
        string eft = "allow",
        string? rlsFilter = null,
        ConsentRowFilter? correlatedRowFilter = null)
    {
        ValidateSubRuleTokens(subRule, rlsFilter);

        if (!_modelSupportsWildcardTenant)
        {
            throw new InvalidOperationException("Das Casbin-Modell unterstützt keine Wildcard-Mandanten (Probe W1). `*`-Regeln sind nicht erlaubt.");
        }

        var ruleMeta = new CasbinRuleMetadata(sub, WildcardTenant, obj, act, subRule, eft, rlsFilter, correlatedRowFilter);

        Publish(src =>
        {
            var currentList = src.ProgrammaticRules.TryGetValue(WildcardTenant, out var list)
                ? list
                : ImmutableArray<CasbinRuleMetadata>.Empty;
            var nextList = currentList.Add(ruleMeta);
            var nextDict = src.ProgrammaticRules.SetItem(WildcardTenant, nextList);
            return src with { ProgrammaticRules = nextDict };
        });
    }

    public void AddWildcardRoleForUser(string user, string role)
    {
        var grouping = new GroupingRule(user, role);

        Publish(src =>
        {
            var currentList = src.ProgrammaticGrouping.TryGetValue(WildcardTenant, out var list)
                ? list
                : ImmutableArray<GroupingRule>.Empty;
            var nextList = currentList.Add(grouping);
            var nextDict = src.ProgrammaticGrouping.SetItem(WildcardTenant, nextList);
            return src with { ProgrammaticGrouping = nextDict };
        });
    }

    private const string RequestedAction = "read";

    public ValueTask<TableAccessDecision> EvaluatePolicyAsync(
        SecurityEvaluationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snapshot = Volatile.Read(ref _currentSnapshot);

        var tableStr = context.TargetTable.ToString();
        var groupsStr = context.GroupSids != null && context.GroupSids.Count > 0
            ? string.Join(",", context.GroupSids.Select(g => g.Value).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty;
        var attrsStr = context.Attributes != null && context.Attributes.Count > 0
            ? string.Join(";", context.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            : string.Empty;
        var cacheKey = $"{snapshot.Epoch}:{context.Tenant.Value}:{context.UserSid.Value}:{tableStr}:{context.PurposeId}:{context.Department}:{context.Region}:{context.ClearanceLevel}:{context.ClientIp}:G[{groupsStr}]:A[{attrsStr}]";

        if (_decisionCache.TryGetValue(cacheKey, out var cachedEntry))
        {
            if (Stopwatch.GetElapsedTime(cachedEntry.CreatedTimestampTicks) <= DecisionCacheTtl)
            {
                return ValueTask.FromResult(cachedEntry.Decision);
            }
            _decisionCache.TryRemove(cacheKey, out _);
        }

        if (_decisionCache.Count >= 10_000)
        {
            _decisionCache.Clear();
        }

        var sw = Stopwatch.StartNew();
        var enforcer = snapshot.Enforcers.TryGetValue(context.Tenant.Value, out var existing)
            ? existing
            : snapshot.WildcardEnforcer;

        var tenantRulesSnapshot = new List<CasbinRuleMetadata>();
        if (snapshot.Rules.TryGetValue("*", out var wildcardRulesList))
        {
            tenantRulesSnapshot.AddRange(wildcardRulesList);
        }
        if (!string.Equals(context.Tenant.Value, "*", StringComparison.OrdinalIgnoreCase) &&
            snapshot.Rules.TryGetValue(context.Tenant.Value, out var tenantRulesList))
        {
            tenantRulesSnapshot.AddRange(tenantRulesList);
        }

        // Subjects evaluated exactly like the Casbin request: the user itself and each of its groups.
        var subjects = new List<string> { context.UserSid.Value };
        if (context.GroupSids != null)
        {
            subjects.AddRange(context.GroupSids.Select(g => g.Value));
        }

        // SEC (Low): Decisions that depend on time-based sub_rules must not be served from the decision cache.
        bool cacheable = !tenantRulesSnapshot.Any(r => ReferencesTime(r.SubRule));

        bool allowed = false;
        var matchedAllowRules = new List<CasbinRuleMetadata>();

        try
        {
            // First check if any deny policy matches for the user or their groups (Deny takes absolute precedence)
            bool denied = false;
            foreach (var rule in tenantRulesSnapshot)
            {
                // Deny rules are matched conservatively (any action, wildcard tenant or current tenant).
                if (!string.Equals(rule.Eft, "deny", StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(rule.Tenant, "*", StringComparison.OrdinalIgnoreCase) && !string.Equals(rule.Tenant, context.Tenant.Value, StringComparison.OrdinalIgnoreCase)) ||
                    !MatchObjectPattern(rule.Obj, tableStr))
                {
                    continue;
                }

                foreach (var subject in subjects)
                {
                    if (!IsSubjectMatch(rule.Sub, subject, enforcer))
                    {
                        continue;
                    }

                    // Fail-closed: a deny rule whose condition cannot be evaluated is treated as matching.
                    if (EvaluateSubRule(rule.SubRule, context, subject, tableStr, RequestedAction) != false)
                    {
                        denied = true;
                        break;
                    }
                }

                if (denied)
                {
                    break;
                }
            }

            if (!denied)
            {
                // SEC H-12: The allow decision and the RLS filter collection use ONE matcher. The set of matching allow
                // rules determined here is both the authorization basis and the source of the RLS filters.
                foreach (var rule in tenantRulesSnapshot)
                {
                    if (IsAllowRuleMatch(rule, context, subjects, tableStr, enforcer))
                    {
                        matchedAllowRules.Add(rule);
                    }
                }

                // Casbin itself remains an additional (AND) gate: if Casbin denies (e.g. Casbin-only deny semantics,
                // role hierarchies), access is denied. If Casbin allows but no allow rule matched in the gateway
                // matcher (e.g. keyMatch2 treating '.' as regex wildcard, see M-18), access is denied as well,
                // because the RLS filters of the Casbin-matched rule could not be collected (fail-closed).
                bool casbinAllowed = false;
                foreach (var subject in subjects)
                {
                    // r = sub, tenant, obj, act, ctx
                    if (enforcer.Enforce(subject, context.Tenant.Value, tableStr, RequestedAction, context))
                    {
                        casbinAllowed = true;
                        break;
                    }
                }

                allowed = casbinAllowed && matchedAllowRules.Count > 0;
            }
        }
        catch (Exception)
        {
            allowed = false;
        }

        sw.Stop();
        GatewayDiagnostics.PolicyEvaluationDuration.Record(sw.Elapsed.TotalMilliseconds);

        var activity = Activity.Current;
        if (activity != null)
        {
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "user.sid", context.UserSid.Value);
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "tenant.id", context.Tenant.Value);
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "policy.match", allowed ? "allow" : "deny");
            GatewayDiagnostics.SetSafeTag(activity, "Governance.CasbinAbacEnforcement", "latency.ms", sw.Elapsed.TotalMilliseconds);
        }

        TableAccessDecision decision;

        if (allowed)
        {
            // Resolve any attached RLS pushdown filters from exactly the allow rules that granted access
            var activeRlsFilters = CollectActiveRlsFilters(matchedAllowRules, context, tableStr);

            string? combinedSql = null;
            if (activeRlsFilters.Count > 0)
            {
                combinedSql = activeRlsFilters.Count == 1
                    ? activeRlsFilters[0]
                    : string.Join(" OR ", activeRlsFilters.Select(f => $"({f})"));
            }

            decision = TableAccessDecision.Allowed(
                context.TargetTable,
                new Dictionary<string, ColumnAccessLevel>(),
                rowFilterSql: combinedSql,
                hasUnconstrainedColumnAllow: true);
        }
        else
        {
            GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
            decision = TableAccessDecision.Denied(
                context.TargetTable,
                $"Access to table '{tableStr}' denied by ABAC policy for tenant '{context.Tenant.Value}'.");
        }

        if (cacheable)
        {
            _decisionCache.TryAdd(cacheKey, new CachedDecision(decision, Stopwatch.GetTimestamp()));
        }

        return ValueTask.FromResult(decision);
    }

    private static readonly string[] TimeReferenceTokens = ["Timestamp", "DateTime", "Now", "Hour", "DayOfWeek", "Date"];

    private static bool ReferencesTime(string? subRule)
    {
        if (string.IsNullOrWhiteSpace(subRule))
        {
            return false;
        }

        foreach (var token in TimeReferenceTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsActionMatch(string ruleAct) =>
        string.Equals(ruleAct, "*", StringComparison.Ordinal) ||
        string.Equals(ruleAct, RequestedAction, StringComparison.OrdinalIgnoreCase);

    private static bool IsSubjectMatch(string ruleSub, string subject, Enforcer enforcer) =>
        string.Equals(ruleSub, "*", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ruleSub, subject, StringComparison.OrdinalIgnoreCase) ||
        enforcer.HasRoleForUser(subject, ruleSub);

    private static bool IsAllowRuleMatch(
        CasbinRuleMetadata rule,
        SecurityEvaluationContext context,
        IReadOnlyList<string> subjects,
        string tableStr,
        Enforcer enforcer)
    {
        if (!string.Equals(rule.Eft, "allow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Same tenant condition as the Casbin matcher (r.tenant == p.tenant || p.tenant == "*")
        if (!string.Equals(rule.Tenant, "*", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(rule.Tenant, context.Tenant.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!MatchObjectPattern(rule.Obj, tableStr) || !IsActionMatch(rule.Act))
        {
            return false;
        }

        foreach (var subject in subjects)
        {
            // Sub_rule is evaluated with r.sub = the matched subject (user or group), as in the Casbin request.
            if (IsSubjectMatch(rule.Sub, subject, enforcer) &&
                EvaluateSubRule(rule.SubRule, context, subject, tableStr, RequestedAction) == true)
            {
                return true;
            }
        }

        return false;
    }

    private List<string> CollectActiveRlsFilters(IReadOnlyList<CasbinRuleMetadata> matchedAllowRules, SecurityEvaluationContext context, string tableStr)
    {
        var result = new List<string>();

        foreach (var rule in matchedAllowRules)
        {
            // Handle Correlated Row Filter (Fail-closed on generation failure)
            if (rule.CorrelatedRowFilter != null)
            {
                var subquery = _rlsFilterGenerator.BuildCorrelatedSubquery(
                    rule.CorrelatedRowFilter,
                    context.TargetDialect ?? Autheris.Domain.Common.DatabaseDialect.SqlServer,
                    RowFilterSubqueryStrategy.Exists,
                    isDeny: false);
                if (!string.IsNullOrWhiteSpace(subquery))
                {
                    result.Add(subquery);
                }
                else
                {
                    throw new SecurityException($"Sicherheitsfehler: Korrelierter RLS-Filter für Tabelle '{tableStr}' konnte nicht generiert werden.");
                }
            }

            // Handle Direct RLS SQL Filter with parameter/context interpolation
            if (!string.IsNullOrWhiteSpace(rule.RlsFilter))
            {
                var interpolated = InterpolateRlsFilter(rule.RlsFilter, context);
                if (string.IsNullOrWhiteSpace(interpolated))
                {
                    // Fail-closed: a matched allow rule with an RLS filter must contribute that filter.
                    throw new SecurityException($"Sicherheitsfehler: RLS-Filter für Tabelle '{tableStr}' ergab nach der Interpolation einen leeren Ausdruck.");
                }

                result.Add(interpolated);
            }
        }

        return result;
    }

    /// <summary>
    /// Evaluates a sub_rule. Returns <c>null</c> when the expression cannot be evaluated (callers decide fail-closed).
    /// </summary>
    private static bool? EvaluateSubRule(string? subRule, SecurityEvaluationContext context, string subject, string tableStr, string act)
    {
        if (string.IsNullOrWhiteSpace(subRule) || string.Equals(subRule.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(subRule.Trim(), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var interpreter = new DynamicExpresso.Interpreter();
            // SEC H-12: Same request shape as the Casbin request (r.sub, r.tenant, r.obj, r.act, r.ctx).
            interpreter.SetVariable("r", new { ctx = context, sub = subject, tenant = context.Tenant.Value, obj = tableStr, act });
            interpreter.SetVariable("ctx", context);
            interpreter.SetVariable("context", context);

            var normalized = Regex.Replace(subRule, @"'([^']{2,})'", "\"$1\"");
            var evalResult = interpreter.Eval(normalized);
            return evalResult is bool b ? b : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex SafeClaimValueRegex = new(@"^[a-zA-Z0-9\-_.@: ]{1,256}$", RegexOptions.Compiled);

    private static readonly Regex RlsPlaceholderRegex = new(@"\$\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled);

    private static string SanitizeClaimForSql(string? value, string claimName)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!SafeClaimValueRegex.IsMatch(value))
        {
            throw new SecurityException($"Sicherheitsfehler: Claim '{claimName}' enthält ungültige Zeichen für SQL-RLS-Interpolation.");
        }
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string InterpolateRlsFilter(string filterTemplate, SecurityEvaluationContext context)
    {
        // Fail-Closed: Required attributes MUST be present in security context if referenced in template
        if ((filterTemplate.Contains("${department}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.Department}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.Department))
        {
            throw new SecurityException($"Sicherheitsfehler: Erforderliches RLS-Attribut 'Department' fehlt im Kontext für Template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${region}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.Region}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.Region))
        {
            throw new SecurityException($"Sicherheitsfehler: Erforderliches RLS-Attribut 'Region' fehlt im Kontext für Template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${clearance}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.ClearanceLevel}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.ClearanceLevel))
        {
            throw new SecurityException($"Sicherheitsfehler: Erforderliches RLS-Attribut 'ClearanceLevel' fehlt im Kontext für Template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${purpose}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.PurposeId}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.PurposeId))
        {
            throw new SecurityException($"Sicherheitsfehler: Erforderliches RLS-Attribut 'PurposeId' fehlt im Kontext für Template '{filterTemplate}'.");
        }

        // SEC M-19: Placeholders are resolved by a single left-to-right scan that tracks SQL string-literal state.
        // A value is ALWAYS emitted as SQL string literal content: inside an existing literal ('${x}', '${x}%') it is
        // ''-escaped; outside a literal (cost_center = ${attr.cost_center}) it is wrapped in quotes. A claim value can
        // therefore never change the SQL structure, even for unquoted templates.
        var sb = new StringBuilder(filterTemplate.Length + 32);
        bool inLiteral = false;
        int i = 0;
        while (i < filterTemplate.Length)
        {
            char c = filterTemplate[i];

            if (c == '\'')
            {
                // '' inside a literal is an escaped quote and does not end the literal
                if (inLiteral && i + 1 < filterTemplate.Length && filterTemplate[i + 1] == '\'')
                {
                    sb.Append("''");
                    i += 2;
                    continue;
                }

                inLiteral = !inLiteral;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '$' && i + 1 < filterTemplate.Length && filterTemplate[i + 1] == '{')
            {
                var match = RlsPlaceholderRegex.Match(filterTemplate, i);
                if (!match.Success || match.Index != i)
                {
                    throw new SecurityException($"Sicherheitsfehler: Ungültiger RLS-Parameter im Template '{filterTemplate}'.");
                }

                var name = match.Groups[1].Value;
                var value = ResolveRlsPlaceholder(name, context)
                    ?? throw new SecurityException($"Sicherheitsfehler: Unaufgelöster RLS-Parameter im Template '{filterTemplate}'.");

                var escaped = SanitizeClaimForSql(value, name);
                if (inLiteral)
                {
                    sb.Append(escaped);
                }
                else
                {
                    sb.Append('\'').Append(escaped).Append('\'');
                }

                i += match.Length;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (inLiteral)
        {
            throw new SecurityException($"Sicherheitsfehler: Nicht abgeschlossenes String-Literal im RLS-Template '{filterTemplate}'.");
        }

        return sb.ToString();
    }

    private static string? ResolveRlsPlaceholder(string name, SecurityEvaluationContext context)
    {
        switch (name.ToLowerInvariant())
        {
            case "r.sub":
            case "user_sid":
                return context.UserSid.Value;
            case "r.tenant":
            case "tenant":
                return context.Tenant.Value;
            case "r.ctx.department":
            case "department":
                return context.Department ?? string.Empty;
            case "r.ctx.region":
            case "region":
                return context.Region ?? string.Empty;
            case "r.ctx.clearancelevel":
            case "clearance":
                return context.ClearanceLevel ?? string.Empty;
            case "r.ctx.purposeid":
            case "purpose":
                return context.PurposeId ?? string.Empty;
        }

        if (context.Attributes == null)
        {
            return null;
        }

        var key = name.StartsWith("attr.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
        foreach (var (k, v) in context.Attributes)
        {
            if (v != null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Gateway object matcher (SEC M-18/H-12). Supports:
    /// <c>*</c> (everything), exact match, <c>prefix.*</c> (any object strictly below <c>prefix.</c>, segment-bounded),
    /// <c>*</c> inside a segment (matches within one segment only) and keyMatch2-style <c>:name</c> segment parameters.
    /// Dots are always literal.
    /// </summary>
    private static bool MatchObjectPattern(string pattern, string target)
    {
        if (string.Equals(pattern, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool trailingWildcard = pattern.EndsWith(".*", StringComparison.Ordinal);
        var body = trailingWildcard ? pattern[..^2] : pattern;

        if (trailingWildcard && body.IndexOf('*', StringComparison.Ordinal) < 0 && body.IndexOf(':', StringComparison.Ordinal) < 0)
        {
            // SEC M-18: 'finance.dbo.*' must not match 'finance.dbo_hr.salaries' -> compare with the segment separator.
            return target.StartsWith(body + ".", StringComparison.OrdinalIgnoreCase) && target.Length > body.Length + 1;
        }

        if (!trailingWildcard && pattern.IndexOf('*', StringComparison.Ordinal) < 0 && pattern.IndexOf(':', StringComparison.Ordinal) < 0)
        {
            return false;
        }

        var regex = new StringBuilder("^");
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '*')
            {
                regex.Append("[^.]*");
                i++;
            }
            else if (c == ':' && (i == 0 || body[i - 1] == '.'))
            {
                int j = i + 1;
                while (j < body.Length && (char.IsLetterOrDigit(body[j]) || body[j] == '_'))
                {
                    j++;
                }

                if (j > i + 1)
                {
                    regex.Append("[^.]+");
                    i = j;
                }
                else
                {
                    regex.Append(Regex.Escape(":"));
                    i++;
                }
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        if (trailingWildcard)
        {
            regex.Append(@"\..+");
        }

        regex.Append('$');

        try
        {
            return Regex.IsMatch(target, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Raised when a file-based (hot) reload was rejected. The last-known-good policy set stays active.
    /// </summary>
    public event Action<string, Exception>? OnPolicyReloadFailed;

    /// <summary>
    /// Splits a policy line at top-level commas only. Commas inside single/double quoted strings and inside
    /// (), [] or {} are part of the field (RR-L4-05: e.g. <c>region IN ('EU','US')</c>).
    /// Unbalanced quotes or brackets are rejected (typical for truncated/partially written files).
    /// </summary>
    internal static List<string> SplitPolicyLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var depth = 0;

        foreach (var c in line)
        {
            if (quote.HasValue)
            {
                current.Append(c);
                if (c == quote.Value)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    current.Append(c);
                    break;
                case '(':
                case '[':
                case '{':
                    depth++;
                    current.Append(c);
                    break;
                case ')':
                case ']':
                case '}':
                    depth--;
                    if (depth < 0)
                    {
                        throw new FormatException("Casbin policy line has unbalanced brackets.");
                    }

                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        if (quote.HasValue || depth != 0)
        {
            throw new FormatException("Casbin policy line has unbalanced quotes or brackets.");
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static string NormalizeEffect(string? rawEft)
    {
        if (string.IsNullOrWhiteSpace(rawEft))
        {
            return "allow";
        }

        if (string.Equals(rawEft, "allow", StringComparison.OrdinalIgnoreCase))
        {
            return "allow";
        }

        if (string.Equals(rawEft, "deny", StringComparison.OrdinalIgnoreCase))
        {
            return "deny";
        }

        // RR-L4-05: unknown effects must never be silently ignored (a mis-parsed DENY would vanish).
        throw new FormatException("Casbin policy effect must be 'allow' or 'deny'.");
    }

    private (ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules, ImmutableArray<GroupingRule> Grouping) ParsePolicyText(
        string policyText,
        string? defaultTenant = null)
    {
        ArgumentNullException.ThrowIfNull(policyText);

        var lines = policyText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rulesBuilder = new Dictionary<string, List<CasbinRuleMetadata>>(StringComparer.OrdinalIgnoreCase);
        var groupingBuilder = new List<GroupingRule>();

        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            List<string> parts;
            try
            {
                parts = SplitPolicyLine(line);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Casbin policy line {lineNumber}: {ex.Message}", ex);
            }

            var type = parts[0].ToLowerInvariant();
            if (type == "p")
            {
                if (parts.Count < 4 || parts.Count > 8)
                {
                    throw new FormatException($"Casbin policy line {lineNumber}: 'p' rules require 4 to 8 fields.");
                }

                var sub = parts[1];
                if (string.IsNullOrWhiteSpace(sub))
                {
                    throw new FormatException($"Casbin policy line {lineNumber}: subject must not be empty.");
                }

                var specifiedTenant = parts.Count > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;

                string ruleTenant;
                string obj;
                string act;
                string subRule;
                string eft;
                string? rlsFilter;

                if (defaultTenant != null && specifiedTenant != null && (specifiedTenant.Contains('.') || specifiedTenant.Contains(':')))
                {
                    // Format without explicit tenant field: p, sub, obj, act, [eft], [subRule]
                    ruleTenant = defaultTenant;
                    obj = parts[2];
                    act = parts.Count > 3 ? parts[3] : "read";
                    if (parts.Count > 4 && (string.Equals(parts[4], "allow", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[4], "deny", StringComparison.OrdinalIgnoreCase)))
                    {
                        eft = NormalizeEffect(parts[4]);
                        subRule = parts.Count > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "true";
                    }
                    else
                    {
                        subRule = parts.Count > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4] : "true";
                        eft = parts.Count > 5 ? NormalizeEffect(parts[5]) : "allow";
                    }
                    rlsFilter = parts.Count > 6 && !string.IsNullOrWhiteSpace(parts[6]) ? parts[6] : null;
                }
                else
                {
                    if (defaultTenant != null)
                    {
                        if (specifiedTenant != null && !string.Equals(specifiedTenant, defaultTenant, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FormatException($"Casbin policy line {lineNumber}: tenant policy file for '{defaultTenant}' cannot define rules for '{specifiedTenant}'.");
                        }
                        ruleTenant = defaultTenant;
                    }
                    else
                    {
                        ruleTenant = specifiedTenant ?? "*";
                    }

                    if (ruleTenant == "*" && !_modelSupportsWildcardTenant)
                    {
                        throw new InvalidOperationException("Das Casbin-Modell unterstützt keine Wildcard-Mandanten (Probe W1). `*`-Regeln sind nicht erlaubt.");
                    }

                    obj = parts.Count > 3 ? parts[3] : "*";
                    act = parts.Count > 4 ? parts[4] : "read";
                    subRule = parts.Count > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5] : "true";
                    try
                    {
                        eft = NormalizeEffect(parts.Count > 6 ? parts[6] : null);
                    }
                    catch (FormatException ex)
                    {
                        throw new FormatException($"Casbin policy line {lineNumber}: {ex.Message}", ex);
                    }
                    rlsFilter = parts.Count > 7 && !string.IsNullOrWhiteSpace(parts[7]) ? parts[7] : null;
                }

                ValidateSubRuleTokens(subRule, rlsFilter);

                if (!rulesBuilder.TryGetValue(ruleTenant, out var list))
                {
                    list = new List<CasbinRuleMetadata>();
                    rulesBuilder[ruleTenant] = list;
                }

                list.Add(new CasbinRuleMetadata(sub, ruleTenant, obj, act, subRule, eft, rlsFilter, null));
            }
            else if (type == "g")
            {
                if (parts.Count != 3 || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]))
                {
                    throw new FormatException($"Casbin policy line {lineNumber}: 'g' rules require exactly 3 fields.");
                }

                groupingBuilder.Add(new GroupingRule(parts[1], parts[2]));
            }
            else
            {
                throw new FormatException($"Casbin policy line {lineNumber}: unknown rule type.");
            }
        }

        var rules = rulesBuilder.ToImmutableDictionary(
            kv => kv.Key,
            kv => kv.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);

        return (rules, groupingBuilder.ToImmutableArray());
    }

    public void LoadPolicyFromText(TenantId tenant, string policyText)
    {
        ArgumentNullException.ThrowIfNull(policyText);

        var (rules, grouping) = ParsePolicyText(policyText, tenant.Value);

        Publish(
            src =>
            {
                var tenantRules = rules.TryGetValue(tenant.Value, out var r)
                    ? r
                    : ImmutableArray<CasbinRuleMetadata>.Empty;

                var nextTenantFiles = src.TenantFiles.SetItem(tenant.Value, tenantRules);
                var nextTenantGrouping = src.TenantGrouping.SetItem(tenant.Value, grouping);

                return src with
                {
                    TenantFiles = nextTenantFiles,
                    TenantGrouping = nextTenantGrouping
                };
            },
            (oldSources, newSources) =>
            {
                var tenantRules = newSources.TenantFiles.TryGetValue(tenant.Value, out var r) ? r : ImmutableArray<CasbinRuleMetadata>.Empty;
                var tenantGrouping = newSources.TenantGrouping.TryGetValue(tenant.Value, out var g) ? g : ImmutableArray<GroupingRule>.Empty;

                bool hadRules = oldSources.TenantFiles.TryGetValue(tenant.Value, out var oldR) && oldR.Length > 0;
                if (tenantRules.Length == 0 && hadRules)
                {
                    _logger?.LogWarning("Casbin tenant policy reload rejected: the new policy set has no 'p' rules while active policies exist for tenant {Tenant}. Preserving last-known-good.", tenant.Value);
                    throw new InvalidOperationException(
                        $"Casbin policy reload rejected: the new policy set has no 'p' rules while active policies exist for tenant '{tenant.Value}'. " +
                        "The last-known-good policy set remains active.");
                }

                if (tenantRules.Length == 0 && tenantGrouping.Length == 0 && oldSources.TenantHasPolicies(tenant.Value))
                {
                    _logger?.LogWarning("Casbin tenant policy reload rejected: empty policy while active policies exist for tenant {Tenant}. Preserving last-known-good.", tenant.Value);
                    throw new InvalidOperationException(
                        "Casbin policy reload rejected: the new policy set is empty while the tenant has active policies. " +
                        "The last-known-good policy set remains active.");
                }
            });
    }

    public void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Casbin policy file not found: {filePath}", filePath);
        }

        _tenantPolicyFiles[tenant.Value] = Path.GetFullPath(filePath);
        var content = File.ReadAllText(filePath);
        LoadPolicyFromText(tenant, content);

        if (watchFile)
        {
            EnableFileWatcher(tenant, filePath);
        }
    }

    public void LoadPolicyFromFile(string filePath, bool watchFile = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Casbin policy file not found: {filePath}", filePath);
        }

        _globalPolicyFilePath = Path.GetFullPath(filePath);
        var content = File.ReadAllText(filePath);
        LoadPolicyFromText(content);

        if (watchFile)
        {
            EnableGlobalFileWatcher(filePath);
        }
    }

    public void LoadPolicyFromText(string policyText)
    {
        ArgumentNullException.ThrowIfNull(policyText);

        var (rules, grouping) = ParsePolicyText(policyText, defaultTenant: null);

        Publish(
            src => src with
            {
                GlobalRules = rules,
                GlobalGrouping = grouping
            },
            (oldSources, newSources) =>
            {
                int totalPRules = newSources.GlobalRules.Values.Sum(r => r.Length);
                int oldGlobalPRules = oldSources.GlobalRules.Values.Sum(r => r.Length);

                if (oldGlobalPRules > 0 && totalPRules == 0)
                {
                    if (newSources.GlobalRules.Count == 0 && newSources.GlobalGrouping.Length == 0)
                    {
                        _logger?.LogWarning("Casbin policy reload rejected: the new policy set is empty while active policies exist. Preserving last-known-good.");
                        throw new InvalidOperationException(
                            "Casbin policy reload rejected: the new policy set is empty while active policies exist. " +
                            "The last-known-good policy set remains active.");
                    }

                    _logger?.LogWarning("Casbin policy reload rejected: the new policy set has no 'p' rules while active policies exist. Preserving last-known-good.");
                    throw new InvalidOperationException(
                        "Casbin policy reload rejected: the new policy set has no 'p' rules while active policies exist. " +
                        "The last-known-good policy set remains active.");
                }
            });
    }

    private void EnableGlobalFileWatcher(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);

        if (_globalFileWatcher != null)
        {
            _globalFileWatcher.Dispose();
        }

        if (_globalDebounceTimer != null)
        {
            _globalDebounceTimer.Dispose();
        }

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        var debounceTimer = new System.Timers.Timer(100) { AutoReset = false };
        debounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    var text = File.ReadAllText(fullPath);
                    LoadPolicyFromText(text);
                }
                else
                {
                    var fnf = new FileNotFoundException("Casbin policy file not found during hot reload.", fullPath);
                    _logger?.LogError(fnf, "Casbin global policy file not found during hot reload: {Path}", fullPath);
                    OnPolicyReloadFailed?.Invoke("*", fnf);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Casbin global policy reload failed.");
                OnPolicyReloadFailed?.Invoke("*", ex);
            }
        };

        void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += (_, _) =>
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        _globalFileWatcher = watcher;
        _globalDebounceTimer = debounceTimer;
    }

    private void EnableFileWatcher(TenantId tenant, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);

        if (_fileWatchers.TryRemove(tenant.Value, out var existingWatcher))
        {
            existingWatcher.Dispose();
        }

        if (_debounceTimers.TryRemove(tenant.Value, out var existingTimer))
        {
            existingTimer.Dispose();
        }

        var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        var debounceTimer = new System.Timers.Timer(100) { AutoReset = false };
        debounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    var text = File.ReadAllText(fullPath);
                    LoadPolicyFromText(tenant, text);
                }
                else
                {
                    // RR-L4-05: a vanished file (rename-based atomic write in progress, ConfigMap swap) never clears policies.
                    var fnf = new FileNotFoundException("Casbin policy file not found during hot reload.", fullPath);
                    _logger?.LogError(fnf, "Casbin policy file not found during hot reload for tenant {Tenant}: {Path}", tenant.Value, fullPath);
                    OnPolicyReloadFailed?.Invoke(tenant.Value, fnf);
                }
            }
            catch (Exception ex)
            {
                // RR-L4-05: last-known-good stays active; surface the failure instead of silently swallowing it.
                _logger?.LogError(ex, "Casbin policy reload failed for tenant {Tenant}.", tenant.Value);
                OnPolicyReloadFailed?.Invoke(tenant.Value, ex);
            }
        };

        void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        }

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += (_, _) =>
        {
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        _fileWatchers[tenant.Value] = watcher;
        _debounceTimers[tenant.Value] = debounceTimer;
    }

    public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (_tenantPolicyFiles.TryGetValue(tenant.Value, out var filePath))
        {
            if (!File.Exists(filePath))
            {
                // RR-L4-05: never drop the tenant's DENY/RLS rules because the backing file is temporarily missing.
                throw new FileNotFoundException(
                    "Casbin policy file is registered but missing; the last-known-good policy set remains active.",
                    filePath);
            }

            var text = File.ReadAllText(filePath);
            LoadPolicyFromText(tenant, text);
        }
        else if (!string.IsNullOrWhiteSpace(_globalPolicyFilePath) && File.Exists(_globalPolicyFilePath))
        {
            var text = File.ReadAllText(_globalPolicyFilePath);
            LoadPolicyFromText(text);
        }
        else
        {
            Publish(src => src with
            {
                ProgrammaticRules = src.ProgrammaticRules.Remove(tenant.Value),
                ProgrammaticGrouping = src.ProgrammaticGrouping.Remove(tenant.Value)
            });
        }

        return Task.CompletedTask;
    }

    internal int DiagnosticPolicyCount(string tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot.Enforcers.TryGetValue(tenant, out var e))
        {
            return e.GetPolicy().Count();
        }
        return snapshot.WildcardEnforcer.GetPolicy().Count();
    }

    internal object DiagnosticEnforcerIdentity(string tenant)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot.Enforcers.TryGetValue(tenant, out var e))
        {
            return e;
        }
        return snapshot.WildcardEnforcer;
    }

    internal static int DiagnosticEnforcerPolicyCount(object enforcer)
    {
        if (enforcer is Enforcer e)
        {
            return e.GetPolicy().Count();
        }
        return 0;
    }

    public void Dispose()
    {
        _globalDebounceTimer?.Dispose();
        _globalFileWatcher?.Dispose();

        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }
        _debounceTimers.Clear();

        foreach (var watcher in _fileWatchers.Values)
        {
            watcher.Dispose();
        }
        _fileWatchers.Clear();
    }
}

