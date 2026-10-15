namespace Autheris.Application.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class CasbinEnforcementService
{
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
            throw new ArgumentException("Security validation error: Casbin sub_rule exceeds the maximum length of 500 characters.", nameof(subRule));
        }

        if (!SafeSubRulePattern.IsMatch(subRule))
        {
            throw new ArgumentException("Security validation error: Casbin sub_rule contains disallowed characters.", nameof(subRule));
        }

        if (!string.IsNullOrWhiteSpace(rlsFilter))
        {
            if (rlsFilter.Length > 1000)
            {
                throw new ArgumentException("Security validation error: Casbin rls_filter exceeds the maximum length of 1000 characters.", nameof(rlsFilter));
            }

            if (!SafeRlsFilterPattern.IsMatch(rlsFilter))
            {
                throw new ArgumentException("Security validation error: Casbin rls_filter contains disallowed characters.", nameof(rlsFilter));
            }

            foreach (var sqlToken in DangerousSqlTokens)
            {
                if (rlsFilter.Contains(sqlToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Security validation error: Casbin rls_filter contains disallowed SQL expression '{sqlToken.Trim()}'.", nameof(rlsFilter));
                }
            }
        }

        foreach (var token in DangerousSubRuleTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Security validation error: Casbin sub_rule contains disallowed expression '{token}'.", nameof(subRule));
            }

            if (!string.IsNullOrWhiteSpace(rlsFilter) && rlsFilter.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Security validation error: Casbin rls_filter contains disallowed expression '{token}'.", nameof(rlsFilter));
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
            throw new InvalidOperationException("The Casbin model does not support wildcard tenants (probe W1). `*` rules are not allowed.");
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
        }, changedTenant: tenant.Value);
    }

    public void AddPolicies(
        TenantId tenant,
        IEnumerable<CasbinRuleInput> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var ruleList = rules as IReadOnlyList<CasbinRuleInput> ?? rules.ToList();
        if (ruleList.Count == 0) return;

        if (tenant.Value == "*" && !_modelSupportsWildcardTenant)
        {
            throw new InvalidOperationException("The Casbin model does not support wildcard tenants (probe W1). `*` rules are not allowed.");
        }

        var metaList = new List<CasbinRuleMetadata>(ruleList.Count);
        foreach (var rule in ruleList)
        {
            ValidateSubRuleTokens(rule.SubRule, rule.RlsFilter);
            metaList.Add(new CasbinRuleMetadata(
                rule.Sub,
                tenant.Value,
                rule.Obj,
                rule.Act,
                rule.SubRule,
                rule.Eft,
                rule.RlsFilter,
                rule.CorrelatedRowFilter));
        }

        Publish(src =>
        {
            var currentList = src.ProgrammaticRules.TryGetValue(tenant.Value, out var list)
                ? list
                : ImmutableArray<CasbinRuleMetadata>.Empty;
            var nextList = currentList.AddRange(metaList);
            var nextDict = src.ProgrammaticRules.SetItem(tenant.Value, nextList);
            return src with { ProgrammaticRules = nextDict };
        }, changedTenant: tenant.Value);
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
        }, changedTenant: tenant.Value);
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
            throw new InvalidOperationException("The Casbin model does not support wildcard tenants (probe W1). `*` rules are not allowed.");
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
        }, changedTenant: WildcardTenant);
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
        }, changedTenant: WildcardTenant);
    }

    public static string ResolveRequestedAction(SecurityEvaluationContext context)
    {
        if (context.Attributes != null)
        {
            if (context.Attributes.TryGetValue("gql.action", out var gqlAction) && gqlAction != null)
            {
                var s = gqlAction.ToString()?.Trim();
                if (string.Equals(s, "write", StringComparison.OrdinalIgnoreCase)) return "write";
                if (string.Equals(s, "read", StringComparison.OrdinalIgnoreCase)) return "read";
            }

            if (context.Attributes.TryGetValue("action", out var action) && action != null)
            {
                var s = action.ToString()?.Trim();
                if (string.Equals(s, "write", StringComparison.OrdinalIgnoreCase)) return "write";
                if (string.Equals(s, "read", StringComparison.OrdinalIgnoreCase)) return "read";
            }
        }

        return "read";
    }

    public ValueTask<TableAccessDecision> EvaluatePolicyAsync(
        SecurityEvaluationContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var guard = _epochGuard;
        if (guard is null)
        {
            return EvaluateCore(context, useCache: true);
        }

        return guard.GetStatus(context.Tenant.Value) switch
        {
            EpochStatus.Fresh => EvaluateCore(context, useCache: true),
            EpochStatus.Degraded => EvaluateCore(context, useCache: false),
            _ => EvaluateAfterEpochCheckAsync(guard, context, ct)
        };
    }

    private async ValueTask<TableAccessDecision> EvaluateAfterEpochCheckAsync(
        PolicyEpochGuard guard,
        SecurityEvaluationContext context,
        CancellationToken ct)
    {
        var tenant = context.Tenant.Value;
        var outcome = await guard.CheckAsync(
            tenant,
            (tenantChanged, wildcardChanged) => ReloadFromClusterSignal(tenant, tenantChanged, wildcardChanged),
            ct).ConfigureAwait(false);

        // Unavailable: the cluster epoch is unknown, so neither read nor write the decision cache.
        return await EvaluateCore(context, useCache: outcome.Kind != EpochCheckKind.Unavailable).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads only the file-backed policy sources after another node changed policy. Programmatic rules are never touched
    /// here (the <see cref="ReloadPoliciesAsync"/> branch that drops them is deliberately not used), and the reload does not
    /// propagate again, so nodes do not ping-pong. Returns false when the reload failed and must be retried.
    /// </summary>
    private bool ReloadFromClusterSignal(string tenant, bool tenantChanged, bool wildcardChanged)
    {
        try
        {
            var reloaded = false;
            if (tenantChanged && _tenantPolicyFiles.TryGetValue(tenant, out var tenantFile))
            {
                if (!File.Exists(tenantFile))
                {
                    _logger?.LogWarning("Casbin tenant policy file for {Tenant} is missing; keeping the last-known-good policy set.", tenant);
                    return false;
                }

                LoadPolicyFromTextCore(new TenantId(tenant), File.ReadAllText(tenantFile), propagate: false);
                reloaded = true;
            }

            var globalFile = _globalPolicyFilePath;
            if (wildcardChanged && !string.IsNullOrWhiteSpace(globalFile))
            {
                if (!File.Exists(globalFile))
                {
                    _logger?.LogWarning("Casbin global policy file is missing; keeping the last-known-good policy set.");
                    return false;
                }

                LoadPolicyFromTextCore(File.ReadAllText(globalFile), propagate: false);
                reloaded = true;
            }

            if (!reloaded)
            {
                // Nothing file-backed to re-read: still drop cached decisions and move to a new snapshot epoch.
                Publish(src => src, changedTenant: tenantChanged ? tenant : null, propagate: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Casbin policy reload after a cluster epoch change failed; the last-known-good policy set remains active.");
            return false;
        }
    }

    private ValueTask<TableAccessDecision> EvaluateCore(SecurityEvaluationContext context, bool useCache)
    {
        var snapshot = Volatile.Read(ref _currentSnapshot);
        var requestedAction = ResolveRequestedAction(context);

        var tableStr = context.TargetTable.ToString();
        var cacheKey = DecisionKey.Create(snapshot.Epoch, context, requestedAction, tableStr);
        useCache &= cacheKey.IsCacheable;

        if (useCache && _decisionCache.TryGet(cacheKey, out var cachedDecision))
        {
            Interlocked.Increment(ref _decisionCacheHits);
            return ValueTask.FromResult(cachedDecision);
        }

        var sw = Stopwatch.StartNew();

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

        var roleClosure = snapshot.GetRoleClosure(context.Tenant.Value);

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
            // AR-12: Lock-free pure stateless evaluation over immutable snapshot rules and role closure.
            // First check if any deny policy matches for the user or their groups (Deny takes absolute precedence)
            bool denied = false;
            foreach (var rule in tenantRulesSnapshot)
            {
                // Deny rules are matched conservatively (matching action or wildcard, wildcard tenant or current tenant).
                if (!string.Equals(rule.Eft, "deny", StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(rule.Tenant, "*", StringComparison.OrdinalIgnoreCase) && !string.Equals(rule.Tenant, context.Tenant.Value, StringComparison.OrdinalIgnoreCase)) ||
                    !MatchObjectPattern(rule.Obj, tableStr) ||
                    !IsActionMatch(rule.Act, requestedAction, isDeny: true))
                {
                    continue;
                }

                foreach (var subject in subjects)
                {
                    if (!IsSubjectMatch(rule.Sub, subject, roleClosure))
                    {
                        continue;
                    }

                    // Fail-closed: a deny rule whose condition cannot be evaluated is treated as matching.
                    if (EvaluateSubRule(rule.SubRule, context, subject, tableStr, requestedAction) != false)
                    {
                        denied = true;
                        break;
                    }
                }

                if (denied) break;
            }

            if (!denied)
            {
                // SEC H-12: The allow decision and the RLS filter collection use ONE matcher. The set of matching allow
                // rules determined here is both the authorization basis and the source of the RLS filters.
                foreach (var rule in tenantRulesSnapshot)
                {
                    if (IsAllowRuleMatch(rule, context, subjects, tableStr, requestedAction, roleClosure))
                    {
                        matchedAllowRules.Add(rule);
                    }
                }

                allowed = matchedAllowRules.Count > 0;
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
            var activeRlsFilters = CollectActiveRlsFilters(matchedAllowRules, context, tableStr, out var typedRowPolicy);

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

            // WP-X0l: the typed form of the same filters. The text above stays for the direct consumers; TableAccessPolicy.Restrict
            // merges the typed policy and re-projects the text, so the merged decision has one source.
            if (typedRowPolicy != null)
            {
                decision = decision with { RowPolicy = typedRowPolicy };
            }
        }
        else
        {
            GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
            decision = TableAccessDecision.Denied(
                context.TargetTable,
                $"Access to table '{tableStr}' denied by ABAC policy for tenant '{context.Tenant.Value}'.");
        }

        // E-5: Do not cache decision if the snapshot epoch has changed while evaluating
        if (useCache && cacheable && Volatile.Read(ref _currentSnapshot).Epoch == snapshot.Epoch)
        {
            _decisionCache.Set(cacheKey, decision);
        }

        return ValueTask.FromResult(decision);
    }
}
