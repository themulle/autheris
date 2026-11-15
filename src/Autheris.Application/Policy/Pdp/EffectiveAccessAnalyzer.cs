namespace Autheris.Application.Policy.Pdp;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Shared structural analysis for effective-access reporting and approval-time effect previews:
/// provenance labels, structural grant redundancy, and hard-policy intersection checks.
/// </summary>
internal static class EffectiveAccessAnalyzer
{
    public static bool TryParseObjectPattern(string? pattern, out string? normalizedPattern, out bool isWildcard)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            normalizedPattern = null;
            isWildcard = false;
            return true;
        }

        if (pattern.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
        {
            normalizedPattern = null;
            isWildcard = false;
            return false;
        }

        if (!ObjectPattern.TryParse(pattern, out var parsed, out _))
        {
            normalizedPattern = null;
            isWildcard = false;
            return false;
        }

        normalizedPattern = parsed!.ToString();
        isWildcard = normalizedPattern.Contains('*', StringComparison.Ordinal);
        return true;
    }

    public static bool MatchesObjectPattern(string? normalizedPattern, TableIdentifier table)
    {
        if (string.IsNullOrWhiteSpace(normalizedPattern))
        {
            return true;
        }

        return ObjectPattern.TryParse(normalizedPattern, out var parsed, out _) && parsed!.MatchesObject(table);
    }

    public static string? EncodeCursor(string? lastObjectName) =>
        string.IsNullOrWhiteSpace(lastObjectName)
            ? null
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(lastObjectName))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

    public static bool TryDecodeCursor(string? cursor, out string? lastObjectName)
    {
        lastObjectName = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 0: break;
                default: return false;
            }

            var bytes = Convert.FromBase64String(padded);
            var decoded = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(decoded) || decoded.Any(char.IsControl))
            {
                return false;
            }

            lastObjectName = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static int CountParameters(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return 0;
        }

        int total = 0;
        foreach (var kv in parameters)
        {
            total += kv.Value.ValueKind == JsonValueKind.Array
                ? kv.Value.GetArrayLength()
                : kv.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? 0 : 1;
        }

        return total;
    }

    public static string ResolveProvenanceVia(
        Consent consent,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<Sid>? allUserSids)
    {
        _ = groupSids;
        if (consent.GranteeType == GranteeType.User ||
            consent.GranteeSid == userSid ||
            (consent.GranteeType != GranteeType.Group &&
             consent.GranteeSid is { } sid &&
             allUserSids?.Contains(sid) == true))
        {
            return "user";
        }

        if (consent.GranteeType == GranteeType.Group && consent.GranteeSid is { } groupSid)
        {
            return groupSid.Value;
        }

        return consent.GranteeSid?.Value ?? consent.GranteeType.ToString();
    }

    public static string ToColumnStateName(ColumnAccessLevel level) => level switch
    {
        ColumnAccessLevel.Clear => "Clear",
        ColumnAccessLevel.Mask => "Masked",
        ColumnAccessLevel.RowDependent => "RowDependent",
        _ => "NoAccess"
    };

    public static IReadOnlyList<RedundantGrantFlag> FindRedundantGrants(
        IReadOnlyList<Consent> grants,
        TableMetadata table)
    {
        if (grants.Count < 2)
        {
            return Array.Empty<RedundantGrantFlag>();
        }

        var flags = new List<RedundantGrantFlag>();
        var ordered = grants
            .Where(static c => c.Effect == ConsentEffect.Allow)
            .OrderBy(static c => c.Id)
            .ToList();

        foreach (var candidate in ordered)
        {
            Consent? covering = null;
            foreach (var other in ordered)
            {
                if (other.Id == candidate.Id)
                {
                    continue;
                }

                if (DoesGrantCover(other, candidate, table))
                {
                    if (!DoesGrantCover(candidate, other, table) || other.Id.CompareTo(candidate.Id) < 0)
                    {
                        covering = other;
                        break;
                    }
                }
            }

            if (covering is not null)
            {
                flags.Add(new RedundantGrantFlag(candidate.Id, covering.Id));
            }
        }

        return flags;
    }

    public static IReadOnlyList<RedundantGrantFlag> FindRedundantAgainstExisting(
        IReadOnlyList<Consent> candidateGrants,
        IReadOnlyList<Consent> existingGrants,
        TableMetadata table)
    {
        if (candidateGrants.Count == 0 || existingGrants.Count == 0)
        {
            return Array.Empty<RedundantGrantFlag>();
        }

        var activeExisting = existingGrants
            .Where(static c => c.Effect == ConsentEffect.Allow)
            .OrderBy(static c => c.Id)
            .ToList();
        var flags = new List<RedundantGrantFlag>();

        foreach (var candidate in candidateGrants.Where(static c => c.Effect == ConsentEffect.Allow))
        {
            var covering = activeExisting.FirstOrDefault(e => e.Id != candidate.Id && DoesGrantCover(e, candidate, table));
            if (covering is not null)
            {
                flags.Add(new RedundantGrantFlag(candidate.Id, covering.Id));
            }
        }

        return flags;
    }

    public static (IReadOnlyList<EffectiveHardPolicyRef> HardPolicies, bool EmptyHardIntersection) AnalyzeHardPoliciesForObject(
        VirtualFilterSnapshot? snapshot,
        TenantId tenant,
        SecurityPrincipalContext subject,
        TableMetadata table)
    {
        if (snapshot is null || snapshot.Profiles.Count == 0)
        {
            return (Array.Empty<EffectiveHardPolicyRef>(), false);
        }

        var activeProfiles = snapshot.Profiles
            .Where(p => p.TenantId == tenant && p.Status == FilterApprovalStatus.Active)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var matchedProfiles = activeProfiles
            .Where(p => p.GranteeType != GranteeType.Role &&
                        GranteeMatcher.MatchesGrant(p.GranteeType, p.GranteeSid, subject.UserSid, subject.GroupSids, null))
            .ToList();

        if (matchedProfiles.Count == 0)
        {
            return (Array.Empty<EffectiveHardPolicyRef>(), false);
        }

        var filtersByName = snapshot.Filters
            .Where(f => f.TenantId == tenant)
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        var applying = new List<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)>();
        var request = new RowScopeCompileRequest(tenant, subject.UserSid, subject.GroupSids, table, []);

        foreach (var profile in matchedProfiles)
        {
            try
            {
                if (!RowScopeCompiler.MatchesScope(profile.Scope, table.Identifier))
                {
                    continue;
                }

                foreach (var binding in profile.Bindings)
                {
                    if (!filtersByName.TryGetValue(binding.FilterName, out VirtualFilter? filter) || filter is null)
                    {
                        continue;
                    }

                    var (applies, _) = RowScopeCompiler.CheckHardBinding(filter, binding, profile, request);
                    if (applies)
                    {
                        applying.Add((filter, binding, profile));
                    }
                }
            }
            catch (Exception ex) when (ex is GatewaySecurityException or VirtualFilterBindingException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // Advisory structural inspection must not throw out of report/preview.
            }
        }

        if (applying.Count == 0)
        {
            return (Array.Empty<EffectiveHardPolicyRef>(), false);
        }

        var effective = RowScopeCompiler.ApplySupersedes(applying);
        var refs = new List<EffectiveHardPolicyRef>();
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (filter, _, profile) in effective
                     .OrderBy(a => a.Filter.Name, StringComparer.Ordinal)
                     .ThenBy(a => a.Profile.GranteeSid?.Value ?? string.Empty, StringComparer.Ordinal))
        {
            var via = profile.GranteeType == GranteeType.Group && profile.GranteeSid is { } groupSid
                ? groupSid.Value
                : "user";
            var key = $"{filter.Name}\u0001{via}";
            if (seenRefs.Add(key))
            {
                refs.Add(new EffectiveHardPolicyRef(filter.Name, via));
            }
        }

        var emptyIntersection = HasEmptyHardIntersection(effective);
        return (refs, emptyIntersection);
    }

    internal static bool DoesGrantCover(Consent broader, Consent narrower, TableMetadata table) =>
        DoesRowScopeCover(broader, narrower) &&
        DoColumnsCover(broader, narrower, table) &&
        DoWriteRulesCover(broader, narrower);

    private static bool DoesRowScopeCover(Consent broader, Consent narrower)
    {
        if (broader.RowScope.Count == 0 && broader.RowFilters.Count == 0)
        {
            return true;
        }

        if (narrower.RowScope.Count == 0 && narrower.RowFilters.Count == 0)
        {
            return false;
        }

        if (broader.RowScope.Count > 0 && narrower.RowScope.Count > 0)
        {
            if (broader.RowFilters.Count > 0 || narrower.RowFilters.Count > 0)
            {
                return false;
            }

            if (broader.RowScope.Count != 1 || narrower.RowScope.Count != 1)
            {
                return false;
            }

            return DoesScopeEntryCover(broader.RowScope[0], narrower.RowScope[0]);
        }

        if (broader.RowFilters.Count > 0 && narrower.RowFilters.Count > 0 && broader.RowScope.Count == 0 && narrower.RowScope.Count == 0)
        {
            return SerializeRowFilters(broader.RowFilters) == SerializeRowFilters(narrower.RowFilters);
        }

        return false;
    }

    private static bool DoesScopeEntryCover(GrantRowScopeEntry broader, GrantRowScopeEntry narrower)
    {
        if (!string.Equals(broader.PolicyName, narrower.PolicyName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(NormalizeColumnMap(broader.ColumnMap), NormalizeColumnMap(narrower.ColumnMap), StringComparison.Ordinal))
        {
            return false;
        }

        if (broader.Parameters.Count != narrower.Parameters.Count)
        {
            return false;
        }

        foreach (var (key, narrowVal) in narrower.Parameters)
        {
            var broaderMatch = broader.Parameters.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(broaderMatch.Key) || !IsJsonParameterSuperset(broaderMatch.Value, narrowVal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJsonParameterSuperset(JsonElement broader, JsonElement narrower)
    {
        var broaderTokens = ExtractCanonicalTokens(broader);
        var narrowerTokens = ExtractCanonicalTokens(narrower);
        if (broaderTokens.Count == 0 || narrowerTokens.Count == 0)
        {
            return false;
        }

        return narrowerTokens.IsSubsetOf(broaderTokens);
    }

    private static HashSet<string> ExtractCanonicalTokens(JsonElement element)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                set.Add(CanonicalScalar(item));
            }
        }
        else if (element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            set.Add(CanonicalScalar(element));
        }

        return set;
    }

    private static string CanonicalScalar(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => "s:" + (item.GetString() ?? string.Empty),
        JsonValueKind.Number => "n:" + item.GetRawText(),
        JsonValueKind.True => "b:true",
        JsonValueKind.False => "b:false",
        _ => item.GetRawText()
    };

    private static bool DoColumnsCover(Consent broader, Consent narrower, TableMetadata table)
    {
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in table.Columns)
        {
            universe.Add(col.ColumnName);
        }

        foreach (var r in broader.ColumnRules)
        {
            universe.Add(r.ColumnName);
        }

        foreach (var r in narrower.ColumnRules)
        {
            universe.Add(r.ColumnName);
        }

        if (universe.Count == 0)
        {
            return broader.ColumnRules.Count == 0 && !broader.IsPatternGrant
                ? true
                : narrower.ColumnRules.Count == 0 && !narrower.IsPatternGrant ? false : true;
        }

        foreach (var col in universe)
        {
            var broaderLevel = ResolveEffectiveColumnLevel(broader, col, table);
            var narrowerLevel = ResolveEffectiveColumnLevel(narrower, col, table);
            if (broaderLevel < narrowerLevel)
            {
                return false;
            }
        }

        return true;
    }

    private static ColumnAccessLevel ResolveEffectiveColumnLevel(Consent consent, string column, TableMetadata table)
    {
        var metaCol = table.GetColumn(column);
        if (consent.IsPatternGrant && metaCol?.DiscoveryHidden == true)
        {
            return ColumnAccessLevel.Deny;
        }

        var level = ConsentResolutionService.ResolveConsentColumnLevel(consent, column);
        if (level == ColumnAccessLevel.Clear &&
            consent.IsPatternGrant &&
            TableAccessDecision.IsCatalogSensitive(column, table) &&
            !consent.ColumnRules.Any(r =>
                string.Equals(r.ColumnName, column, StringComparison.OrdinalIgnoreCase) &&
                r.AccessLevel == ColumnAccessLevel.Clear))
        {
            return ColumnAccessLevel.Mask;
        }

        return level;
    }

    private static bool DoWriteRulesCover(Consent broader, Consent narrower)
    {
        if (narrower.WriteRules.Count == 0)
        {
            return true;
        }

        if (broader.WriteRules.Count == 0)
        {
            return false;
        }

        var broaderOps = broader.WriteRules.Select(static r => r.Operation).ToHashSet();
        return narrower.WriteRules.All(r => broaderOps.Contains(r.Operation));
    }

    private static string NormalizeColumnMap(IReadOnlyDictionary<string, string>? columnMap)
    {
        if (columnMap is null || columnMap.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            columnMap
                .OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static kv => $"{kv.Key.ToLowerInvariant()}={kv.Value.ToLowerInvariant()}"));
    }

    private static string SerializeRowFilters(IReadOnlyList<ConsentRowFilter> filters) =>
        string.Join(
            ";",
            filters
                .OrderBy(static f => f.ColumnName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static f => f.Operator, StringComparer.OrdinalIgnoreCase)
                .Select(static f => $"{f.ColumnName.ToLowerInvariant()}:{f.Operator.ToUpperInvariant()}:{f.ValueJson ?? string.Empty}"));

    private static bool HasEmptyHardIntersection(
        IReadOnlyList<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)> bindings)
    {
        if (bindings.Count < 2)
        {
            return false;
        }

        var constraintsByTargetKey = new Dictionary<string, KeyColumnConstraint>(StringComparer.OrdinalIgnoreCase);

        foreach (var (filter, binding, _) in bindings)
        {
            if (filter.Structured is null || filter.KeyColumns.Count == 0)
            {
                continue;
            }

            foreach (var qualifiedKey in filter.KeyColumns)
            {
                var rawKeyName = VirtualFilterNames.ColumnOf(qualifiedKey);
                var targetKeyCol = binding.ColumnMap is not null && binding.ColumnMap.TryGetValue(rawKeyName, out var mapped)
                    ? mapped
                    : rawKeyName;

                if (!constraintsByTargetKey.TryGetValue(targetKeyCol, out var state))
                {
                    state = new KeyColumnConstraint();
                    constraintsByTargetKey[targetKeyCol] = state;
                }

                foreach (var cond in filter.Structured.Where)
                {
                    if (!string.Equals(cond.Column, qualifiedKey, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(VirtualFilterNames.ColumnOf(cond.Column), rawKeyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    state.Apply(cond);
                    if (state.IsEmpty)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private sealed class KeyColumnConstraint
    {
        private HashSet<string>? _allowed;
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private bool _mustBeNull;
        private bool _mustBeNonNull;

        public bool IsEmpty
        {
            get
            {
                if (_mustBeNull && _mustBeNonNull)
                {
                    return true;
                }

                if (_allowed is not null)
                {
                    if (_mustBeNull)
                    {
                        return true;
                    }

                    return _allowed.All(_excluded.Contains);
                }

                return false;
            }
        }

        public void Apply(FilterCondition condition)
        {
            switch (condition.Operator)
            {
                case FilterConditionOperator.IsNull:
                    _mustBeNull = true;
                    break;
                case FilterConditionOperator.IsNotNull:
                    _mustBeNonNull = true;
                    break;
                case FilterConditionOperator.Eq:
                    _mustBeNonNull = true;
                    if (!string.IsNullOrEmpty(condition.Value) && !RowPolicyParameterBinder.TryExtractStructuredPlaceholder(condition.Value, out _))
                    {
                        var single = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { condition.Value };
                        if (_allowed is null)
                        {
                            _allowed = single;
                        }
                        else
                        {
                            _allowed.IntersectWith(single);
                        }
                    }

                    break;
                case FilterConditionOperator.NotEq:
                    _mustBeNonNull = true;
                    if (!string.IsNullOrEmpty(condition.Value) && !RowPolicyParameterBinder.TryExtractStructuredPlaceholder(condition.Value, out _))
                    {
                        _excluded.Add(condition.Value);
                    }

                    break;
            }
        }
    }
}
