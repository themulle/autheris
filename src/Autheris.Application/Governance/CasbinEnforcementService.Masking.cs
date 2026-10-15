namespace Autheris.Application.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Governance;

public sealed partial class CasbinEnforcementService
{
    private static bool IsActionMatch(string ruleAct, string requestedAction, bool isDeny = false) =>
        string.Equals(ruleAct, "*", StringComparison.Ordinal) ||
        string.Equals(ruleAct, requestedAction, StringComparison.OrdinalIgnoreCase) ||
        (isDeny && string.Equals(ruleAct, "read", StringComparison.OrdinalIgnoreCase) && string.Equals(requestedAction, "write", StringComparison.OrdinalIgnoreCase));

    private static bool IsSubjectMatch(string ruleSub, string subject, FrozenDictionary<string, FrozenSet<string>> roleClosure) =>
        string.Equals(ruleSub, "*", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ruleSub, subject, StringComparison.OrdinalIgnoreCase) ||
        (roleClosure.TryGetValue(subject, out var roles) && roles.Contains(ruleSub));

    private static bool IsAllowRuleMatch(
        CasbinRuleMetadata rule,
        SecurityEvaluationContext context,
        IReadOnlyList<string> subjects,
        string tableStr,
        string requestedAction,
        FrozenDictionary<string, FrozenSet<string>> roleClosure)
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

        if (!MatchObjectPattern(rule.Obj, tableStr) || !IsActionMatch(rule.Act, requestedAction))
        {
            return false;
        }

        foreach (var subject in subjects)
        {
            // Sub_rule is evaluated with r.sub = the matched subject (user or group), as in the Casbin request.
            if (IsSubjectMatch(rule.Sub, subject, roleClosure) &&
                EvaluateSubRule(rule.SubRule, context, subject, tableStr, requestedAction) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SEC-X1-02: the target dialect is mandatory for a correlated row filter. There is no default (the former SQL Server default
    /// rendered the wrong quoting and strategy for every other source); an unknown dialect fails closed.
    /// </summary>
    private static DatabaseDialect RequireTargetDialect(DatabaseDialect? dialect)
    {
        if (dialect is { } known && Enum.IsDefined(known))
        {
            return known;
        }

        throw new SecurityException("Security error: the target dialect of a correlated row filter is unknown.");
    }

    /// <summary>
    /// Typed form of the correlated row filter (SEC-X1-02): an EXISTS predicate whose values are policy parameters.
    /// <paramref name="ruleOrdinal"/> gives every rule distinct parameter names, so the predicates of several matched allow rules merge.
    /// </summary>
    /// <exception cref="SecurityException">The target dialect is unknown.</exception>
    internal PolicyPredicate BuildCorrelatedRowFilterPredicate(ConsentRowFilter filter, DatabaseDialect? targetDialect, int ruleOrdinal)
    {
        RequireTargetDialect(targetDialect);
        return _rlsFilterGenerator.BuildCorrelatedPredicate(filter, $"__gql_rls_c{ruleOrdinal}_");
    }

    private List<string> CollectActiveRlsFilters(
        IReadOnlyList<CasbinRuleMetadata> matchedAllowRules,
        SecurityEvaluationContext context,
        string tableStr,
        out IRowPolicy? typedRowPolicy)
    {
        var result = new List<string>();

        // WP-X0l: the typed parts are built from the same rule inputs, in the same order. Once one part cannot be typed (no parser,
        // text that is not canonical SQL, a column outside the catalog) the decision stays string-only: the compiler path denies it.
        var typedParts = _rowPolicies != null && context.TargetDialect is { } ? new List<PolicyPredicate>() : null;

        for (int ordinal = 0; ordinal < matchedAllowRules.Count; ordinal++)
        {
            var rule = matchedAllowRules[ordinal];

            // Handle Correlated Row Filter (Fail-closed on generation failure)
            if (rule.CorrelatedRowFilter != null)
            {
                var subquery = _rlsFilterGenerator.BuildCorrelatedSubquery(
                    rule.CorrelatedRowFilter,
                    RequireTargetDialect(context.TargetDialect),
                    RowFilterSubqueryStrategy.Exists,
                    isDeny: false);
                if (!string.IsNullOrWhiteSpace(subquery))
                {
                    result.Add(subquery);
                }
                else
                {
                    throw new SecurityException($"Security error: Correlated row filter for table '{tableStr}' could not be generated.");
                }

                typedParts = TryType(typedParts, () => BuildCorrelatedRowFilterPredicate(rule.CorrelatedRowFilter, context.TargetDialect, ordinal));
            }

            // Handle Direct RLS SQL Filter with parameter/context interpolation
            if (!string.IsNullOrWhiteSpace(rule.RlsFilter))
            {
                var interpolated = InterpolateRlsFilter(rule.RlsFilter, context);
                if (string.IsNullOrWhiteSpace(interpolated))
                {
                    // Fail-closed: a matched allow rule with an RLS filter must contribute that filter.
                    throw new SecurityException($"Security error: Row filter for table '{tableStr}' resulted in an empty expression after interpolation.");
                }

                result.Add(interpolated);
                typedParts = TryType(typedParts, () => _rowPolicies!.ParseAdminPredicate(
                    interpolated,
                    new TableIdentity(context.TargetTable.Schema, context.TargetTable.TableName),
                    context.TableColumns ?? context.RequestedColumns.ToList(),
                    context.TargetDialect!.Value));
            }
        }

        typedRowPolicy = null;
        if (typedParts is { Count: > 0 })
        {
            try
            {
                var combined = typedParts.Aggregate(static (left, right) => left.Or(right));
                typedRowPolicy = _rowPolicies!.FromPredicate(combined, context.TargetDialect!.Value);
            }
            catch (Exception ex) when (ex is SecurityException or ArgumentException or InvalidOperationException)
            {
                typedRowPolicy = null;
            }
        }

        return result;
    }

    /// <summary>Adds the typed part, or drops the whole typed form (returns null) when it cannot be built.</summary>
    private static List<PolicyPredicate>? TryType(List<PolicyPredicate>? parts, Func<PolicyPredicate> build)
    {
        if (parts == null) return null;

        try
        {
            parts.Add(build());
            return parts;
        }
        catch (Exception ex) when (ex is SecurityException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> ObjectPatternRegexes = new(StringComparer.Ordinal);
    private const int MaxObjectPatternRegexes = 20_000;

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
            // WP-PF-03: one precompiled linear-time regex per distinct pattern (the static Regex cache holds only 15 entries).
            var compiled = ObjectPatternRegexes.GetOrAdd(pattern, static (_, expression) =>
                new Regex(
                    expression,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                    TimeSpan.FromMilliseconds(200)),
                regex.ToString());
            if (ObjectPatternRegexes.Count > MaxObjectPatternRegexes)
            {
                ObjectPatternRegexes.Clear();
            }

            return compiled.IsMatch(target);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

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
                if (c == quote.Value) quote = null;
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
        if (string.IsNullOrWhiteSpace(rawEft) || string.Equals(rawEft, "allow", StringComparison.OrdinalIgnoreCase))
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

    public static int ValidatePolicyFile(string policyText, bool modelSupportsWildcardTenant = true)
    {
        var (rules, _) = ParsePolicyText(policyText, defaultTenant: null, modelSupportsWildcardTenant);
        return rules.Values.Sum(r => r.Length);
    }

    private void EnableGlobalFileWatcher(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);

        _globalFileWatcher?.Dispose();
        _globalDebounceTimer?.Dispose();

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
                    LoadPolicyFromText(File.ReadAllText(fullPath));
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
        watcher.Renamed += (_, _) => { debounceTimer.Stop(); debounceTimer.Start(); };

        _globalFileWatcher = watcher;
        _globalDebounceTimer = debounceTimer;
    }

    private void EnableFileWatcher(TenantId tenant, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);

        if (_fileWatchers.TryRemove(tenant.Value, out var existingWatcher)) existingWatcher.Dispose();
        if (_debounceTimers.TryRemove(tenant.Value, out var existingTimer)) existingTimer.Dispose();

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
                    LoadPolicyFromText(tenant, File.ReadAllText(fullPath));
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
        watcher.Renamed += (_, _) => { debounceTimer.Stop(); debounceTimer.Start(); };

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

            LoadPolicyFromText(tenant, File.ReadAllText(filePath));
        }
        else if (!string.IsNullOrWhiteSpace(_globalPolicyFilePath) && File.Exists(_globalPolicyFilePath))
        {
            LoadPolicyFromText(File.ReadAllText(_globalPolicyFilePath));
        }
        else
        {
            Publish(src => src with
            {
                ProgrammaticRules = src.ProgrammaticRules.Remove(tenant.Value),
                ProgrammaticGrouping = src.ProgrammaticGrouping.Remove(tenant.Value)
            }, changedTenant: tenant.Value);
        }

        return Task.CompletedTask;
    }
}
