namespace Autheris.Application.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class CasbinEnforcementService
{
    private static readonly string[] TimeReferenceTokens = ["Timestamp", "DateTime", "Now", "Hour", "DayOfWeek", "Date"];

    private static bool ReferencesTime(string? subRule)
    {
        if (string.IsNullOrWhiteSpace(subRule)) return false;

        foreach (var token in TimeReferenceTokens)
        {
            if (subRule.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        // A rule that references a multi-valued (ambiguous) attribute is unknown: an allow rule does not match, a deny rule matches.
        if (ReferencesAmbiguousAttribute(subRule, context))
        {
            return null;
        }

        // WP-PF-03: compiled once per distinct sub_rule; null (unknown) is handled fail-closed by the callers.
        return CompiledSubRuleCache.Evaluate(subRule, context, subject, tableStr, act);
    }

    private static readonly Regex SafeClaimValueRegex = new(@"^[a-zA-Z0-9\-_.@: ]{1,256}$", RegexOptions.Compiled);
    private static readonly Regex RlsPlaceholderRegex = new(@"\$\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled);

    private static string SanitizeClaimForSql(string? value, string claimName)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (!SafeClaimValueRegex.IsMatch(value))
        {
            throw new SecurityException($"Security error: Claim '{claimName}' contains invalid characters for SQL row filter interpolation.");
        }
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Attribute value for a claim type that carried several distinct values. The evaluator never picks one of them: a sub_rule that
    /// references such an attribute is unknown (fail closed: an allow rule does not match, a deny rule matches) and a row filter
    /// template that references it is rejected, exactly like a missing attribute. The text is constant, so neither the values nor
    /// their order reach a filter, a decision cache key or a log.
    /// </summary>
    public sealed class AmbiguousAttributeValue
    {
        public static readonly AmbiguousAttributeValue Instance = new();

        private AmbiguousAttributeValue()
        {
        }

        public override string ToString() => "<ambiguous>";
    }

    /// <summary>
    /// Adds one claim to a Casbin attribute map (fail closed): an empty or whitespace value is treated as missing, and a claim type
    /// that appears again with a different value becomes <see cref="AmbiguousAttributeValue"/> instead of keeping the last value.
    /// The map should use a case-insensitive comparer, so claim types differing only in case are merged as well.
    /// </summary>
    public static void AddClaimAttribute(IDictionary<string, object?> attributes, string claimType, string? claimValue)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (string.IsNullOrWhiteSpace(claimType) || string.IsNullOrWhiteSpace(claimValue))
        {
            return;
        }

        if (attributes.TryGetValue(claimType, out var existing))
        {
            if (existing is string text && string.Equals(text, claimValue, StringComparison.Ordinal))
            {
                return;
            }

            attributes[claimType] = AmbiguousAttributeValue.Instance;
            return;
        }

        attributes[claimType] = claimValue;
    }

    // Typed context properties read an attribute through a fallback chain (SecurityEvaluationContext.Department reads 'department',
    // then 'dept'); the property is ambiguous when the attribute it resolves to is ambiguous. Each entry lists the names under which a
    // rule or template can reference the property.
    private static readonly (string Primary, string Fallback, string[] References)[] AliasedContextAttributes =
    {
        ("department", "dept", new[] { "Department", "department" }),
        ("region", "country", new[] { "Region", "region" }),
        ("clearance", "clearance_level", new[] { "ClearanceLevel", "clearance" }),
        ("purpose", "purpose_id", new[] { "PurposeId", "purpose" })
    };

    /// <summary>True when <paramref name="text"/> (a sub_rule or row filter template) references an ambiguous attribute.</summary>
    private static bool ReferencesAmbiguousAttribute(string text, SecurityEvaluationContext context)
    {
        var attributes = context.Attributes;
        if (attributes is not { Count: > 0 } || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var any = false;
        foreach (var (key, value) in attributes)
        {
            if (value is AmbiguousAttributeValue)
            {
                any = true;
                if (ContainsIdentifier(text, key))
                {
                    return true;
                }
            }
        }

        if (!any)
        {
            return false;
        }

        foreach (var (primary, fallback, references) in AliasedContextAttributes)
        {
            var resolved = attributes.TryGetValue(primary, out var primaryValue) && primaryValue != null
                ? primaryValue
                : attributes.TryGetValue(fallback, out var fallbackValue) ? fallbackValue : null;
            if (resolved is not AmbiguousAttributeValue)
            {
                continue;
            }

            foreach (var reference in references)
            {
                if (ContainsIdentifier(text, reference))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Case-insensitive occurrence of <paramref name="name"/> that is not part of a longer identifier.</summary>
    private static bool ContainsIdentifier(string text, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var end = index + name.Length;
            var startsWord = index == 0 || !IsIdentifierChar(text[index - 1]);
            var endsWord = end >= text.Length || !IsIdentifierChar(text[end]);
            if (startsWord && endsWord)
            {
                return true;
            }

            index = text.IndexOf(name, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string InterpolateRlsFilter(string filterTemplate, SecurityEvaluationContext context)
    {
        // Fail-Closed: an attribute with several distinct claim values is never resolved to one of them.
        if (ReferencesAmbiguousAttribute(filterTemplate, context))
        {
            throw new SecurityException($"Security error: Row filter template '{filterTemplate}' references an attribute with several distinct values.");
        }

        // Fail-Closed: Required attributes MUST be present in security context if referenced in template
        if ((filterTemplate.Contains("${department}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.Department}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.Department))
        {
            throw new SecurityException($"Security error: Required row filter attribute 'Department' is missing in the context for template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${region}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.Region}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.Region))
        {
            throw new SecurityException($"Security error: Required row filter attribute 'Region' is missing in the context for template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${clearance}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.ClearanceLevel}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.ClearanceLevel))
        {
            throw new SecurityException($"Security error: Required row filter attribute 'ClearanceLevel' is missing in the context for template '{filterTemplate}'.");
        }

        if ((filterTemplate.Contains("${purpose}", StringComparison.OrdinalIgnoreCase) || filterTemplate.Contains("${r.ctx.PurposeId}", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(context.PurposeId))
        {
            throw new SecurityException($"Security error: Required row filter attribute 'PurposeId' is missing in the context for template '{filterTemplate}'.");
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
                    throw new SecurityException($"Security error: Invalid row filter parameter in template '{filterTemplate}'.");
                }

                var name = match.Groups[1].Value;
                var value = ResolveRlsPlaceholder(name, context)
                    ?? throw new SecurityException($"Security error: Unresolved row filter parameter in template '{filterTemplate}'.");

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
            // An empty or whitespace value is missing: the placeholder stays unresolved and the template is rejected (fail closed).
            case "r.ctx.department":
            case "department":
                return NullIfBlank(context.Department);
            case "r.ctx.region":
            case "region":
                return NullIfBlank(context.Region);
            case "r.ctx.clearancelevel":
            case "clearance":
                return NullIfBlank(context.ClearanceLevel);
            case "r.ctx.purposeid":
            case "purpose":
                return NullIfBlank(context.PurposeId);
        }

        if (context.Attributes == null) return null;

        var key = name.StartsWith("attr.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
        string? resolved = null;
        foreach (var (k, v) in context.Attributes)
        {
            if (v == null || !string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Several distinct values (an ambiguous claim, or keys differing only in case in a case-sensitive map) or an empty value
            // never resolve (defense in depth; InterpolateRlsFilter rejects ambiguous references up front).
            var text = v is AmbiguousAttributeValue ? null : NullIfBlank(v.ToString());
            if (text == null || (resolved != null && !string.Equals(resolved, text, StringComparison.Ordinal)))
            {
                return null;
            }

            resolved = text;
        }

        return resolved;
    }

    /// <summary>
    /// Raised when a file-based (hot) reload was rejected. The last-known-good policy set stays active.
    /// </summary>
    public event Action<string, Exception>? OnPolicyReloadFailed;

    private (ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules, ImmutableArray<GroupingRule> Grouping) ParsePolicyText(
        string policyText,
        string? defaultTenant = null) =>
        ParsePolicyText(policyText, defaultTenant, _modelSupportsWildcardTenant);

    internal static (ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules, ImmutableArray<GroupingRule> Grouping) ParsePolicyText(
        string policyText,
        string? defaultTenant,
        bool modelSupportsWildcardTenant,
        bool allowWildcardForDefaultTenant = false)
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
                string ruleTenant, obj, act, subRule, eft;
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
                            if (allowWildcardForDefaultTenant && string.Equals(specifiedTenant, "*", StringComparison.OrdinalIgnoreCase))
                            {
                                ruleTenant = "*";
                            }
                            else
                            {
                                throw new FormatException($"Casbin policy line {lineNumber}: tenant policy file for '{defaultTenant}' cannot define rules for '{specifiedTenant}'.");
                            }
                        }
                        else
                        {
                            ruleTenant = defaultTenant;
                        }
                    }
                    else
                    {
                        ruleTenant = specifiedTenant ?? "*";
                    }

                    if (ruleTenant == "*" && !modelSupportsWildcardTenant)
                    {
                        throw new InvalidOperationException("The Casbin model does not support wildcard tenants (probe W1). `*` rules are not allowed.");
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

    public void LoadPolicyFromText(TenantId tenant, string policyText) =>
        LoadPolicyFromTextCore(tenant, policyText, propagate: true);

    private void LoadPolicyFromTextCore(TenantId tenant, string policyText, bool propagate)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        var (rules, grouping) = ParsePolicyText(policyText, tenant.Value);

        Publish(
            src =>
            {
                var tenantRules = rules.TryGetValue(tenant.Value, out var r) ? r : ImmutableArray<CasbinRuleMetadata>.Empty;
                return src with
                {
                    TenantFiles = src.TenantFiles.SetItem(tenant.Value, tenantRules),
                    TenantGrouping = src.TenantGrouping.SetItem(tenant.Value, grouping)
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
            },
            changedTenant: tenant.Value,
            propagate: propagate);
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
        // Registering a file is node startup, not a policy change: it must not bump the cluster epoch (rolling restarts would
        // otherwise make every node reload and clear its cache).
        LoadPolicyFromTextCore(tenant, content, propagate: false);

        if (watchFile) EnableFileWatcher(tenant, filePath);
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
        LoadPolicyFromTextCore(content, propagate: false);

        if (watchFile) EnableGlobalFileWatcher(filePath);
    }

    public void LoadPolicyFromText(string policyText) =>
        LoadPolicyFromTextCore(policyText, propagate: true);

    private void LoadPolicyFromTextCore(string policyText, bool propagate)
    {
        ArgumentNullException.ThrowIfNull(policyText);
        var (rules, grouping) = ParsePolicyText(policyText, defaultTenant: null);

        Publish(
            src => src with { GlobalRules = rules, GlobalGrouping = grouping },
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
            },
            propagate: propagate);
    }
}
