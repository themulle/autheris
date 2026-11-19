namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

public sealed partial class RowScopeCompiler
{
    private async ValueTask<(
        RowScopeCompileOutcome? Denial,
        PolicyPredicate? Predicate,
        IReadOnlyList<string> AppliedNames,
        IReadOnlyList<AppliedVirtualFilterBinding> AppliedBindings)> CompileHardPoliciesInternalAsync(
        RowScopeCompileRequest request,
        VirtualFilterSnapshot snapshot,
        VirtualFilterTenantContext? tenantContext,
        TargetSqlDialect dialect,
        Func<int> nextSeq,
        CancellationToken ct)
    {
        var table = request.Metadata.Identifier;
        var activeProfiles = snapshot.Profiles
            .Where(p => p.TenantId == request.Tenant && p.Status == FilterApprovalStatus.Active)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var matchedProfiles = activeProfiles
            .Where(p => GranteeMatcher.MatchesRestriction(p.GranteeType, p.GranteeSid, request.UserSid, request.GroupSids, request.AllUserSids))
            .ToList();

        if (matchedProfiles.Count == 0)
        {
            return (null, null, Array.Empty<string>(), Array.Empty<AppliedVirtualFilterBinding>());
        }

        var filtersByName = snapshot.Filters
            .Where(f => f.TenantId == request.Tenant)
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        var applying = new List<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)>();
        foreach (var profile in matchedProfiles)
        {
            try
            {
                if (!MatchesScope(profile.Scope, table))
                {
                    continue;
                }

                bool covered = false;
                (VirtualFilter Filter, IReadOnlyList<string> Missing)? missingBinding = null;
                foreach (var binding in profile.Bindings)
                {
                    if (!filtersByName.TryGetValue(binding.FilterName, out var filter))
                    {
                        return (
                            RowScopeCompileOutcome.Deny(
                                VirtualFilterErrorCodes.BindingMisconfigured,
                                $"Profile '{profile.Name}' binds unknown row policy '{binding.FilterName}'."),
                            null,
                            Array.Empty<string>(),
                            Array.Empty<AppliedVirtualFilterBinding>());
                    }

                    var (applies, missing) = CheckHardBinding(filter, binding, profile, request);
                    if (applies)
                    {
                        covered = true;
                        applying.Add((filter, binding, profile));
                    }
                    else if (missing.Count > 0)
                    {
                        missingBinding ??= (filter, missing);
                    }
                }

                if (!covered && (profile.Uncovered == UncoveredPolicy.Deny || missingBinding != null))
                {
                    var detail = missingBinding != null
                        ? $"{HardPolicyUncoveredCode}: '{table}' matches policy '{missingBinding.Value.Filter.Name}' in profile '{profile.Name}', but lacks required column(s): {string.Join(", ", missingBinding.Value.Missing)}."
                        : $"{HardPolicyUncoveredCode}: '{table}' is in scope of hard row policy profile '{profile.Name}' but uncovered.";
                    return (
                        RowScopeCompileOutcome.Deny(HardPolicyUncoveredCode, detail, DecisionStep.ExplicitDeny),
                        null,
                        Array.Empty<string>(),
                        Array.Empty<AppliedVirtualFilterBinding>());
                }
            }
            catch (VirtualFilterBindingException ex)
            {
                return (
                    RowScopeCompileOutcome.Deny(ex.Code, ex.Message, DecisionStep.ExplicitDeny),
                    null,
                    Array.Empty<string>(),
                    Array.Empty<AppliedVirtualFilterBinding>());
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                var code = ex is RegexMatchTimeoutException
                    ? VirtualFilterErrorCodes.PatternEvaluationFailed
                    : VirtualFilterErrorCodes.BindingMisconfigured;
                return (
                    RowScopeCompileOutcome.Deny(code, ex.Message, DecisionStep.ExplicitDeny),
                    null,
                    Array.Empty<string>(),
                    Array.Empty<AppliedVirtualFilterBinding>());
            }
        }

        if (applying.Count == 0)
        {
            return (null, null, Array.Empty<string>(), Array.Empty<AppliedVirtualFilterBinding>());
        }

        var effective = ApplySupersedes(applying);
        var sorted = effective
            .OrderBy(a => a.Filter.Name, StringComparer.Ordinal)
            .ThenBy(a => a.Binding.TargetPattern ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(a => NormalizeColumnMapKey(a.Binding.ColumnMap), StringComparer.Ordinal)
            .ToList();

        var seenBindings = new HashSet<string>(StringComparer.Ordinal);
        var predicates = new List<PolicyPredicate>();
        var appliedBindings = new List<AppliedVirtualFilterBinding>();

        foreach (var (filter, binding, _) in sorted)
        {
            string dedupeKey = $"{filter.Name}\u0001{binding.TargetPattern ?? string.Empty}\u0001{NormalizeColumnMapKey(binding.ColumnMap)}\u0001{binding.TimeColumn ?? string.Empty}";
            if (!seenBindings.Add(dedupeKey))
            {
                continue;
            }

            try
            {
                string prefix = $"__gql_vf_{nextSeq().ToString(CultureInfo.InvariantCulture)}_";
                PolicyPredicate pred = filter.EvaluationMode == RowPolicyEvaluationMode.KeySet
                    ? await CompileKeySetFilterAsync(filter, binding.ColumnMap, request.Metadata, request.Tenant, dialect, prefix, ct).ConfigureAwait(false)
                    : BuildLiveTypedPredicate(filter, binding, request.Metadata, tenantContext, prefix, parameterValues: null);
                predicates.Add(pred);
                appliedBindings.Add(new AppliedVirtualFilterBinding(filter, binding, snapshot.Generation));
            }
            catch (VirtualFilterBindingException ex)
            {
                return (
                    RowScopeCompileOutcome.Deny(ex.Code, ex.Message, DecisionStep.ExplicitDeny),
                    null,
                    Array.Empty<string>(),
                    Array.Empty<AppliedVirtualFilterBinding>());
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or GatewaySecurityException)
            {
                var code = ex is GatewaySecurityException gse ? gse.ErrorCode : VirtualFilterErrorCodes.BindingMisconfigured;
                return (
                    RowScopeCompileOutcome.Deny(code, ex.Message, DecisionStep.ExplicitDeny),
                    null,
                    Array.Empty<string>(),
                    Array.Empty<AppliedVirtualFilterBinding>());
            }
        }

        var combined = predicates.Count > 0 ? predicates.Aggregate(static (l, r) => l.And(r)) : null;
        var names = appliedBindings.Select(b => b.Filter.Name).Distinct(StringComparer.Ordinal).ToList();
        return (null, combined, names, appliedBindings);
    }

    internal static (bool Applies, IReadOnlyList<string> Missing) CheckHardBinding(
        VirtualFilter filter,
        FilterBinding binding,
        VirtualFilterAccessProfile profile,
        RowScopeCompileRequest request)
    {
        if (filter.Status != FilterApprovalStatus.Active || (binding.ObjectKinds & request.ObjectKind) == 0)
        {
            return (false, Array.Empty<string>());
        }

        var table = request.Metadata.Identifier;
        bool isCrossSource = !string.Equals(table.Domain, filter.Source, StringComparison.OrdinalIgnoreCase);
        if (isCrossSource)
        {
            if (filter.EvaluationMode == RowPolicyEvaluationMode.Live &&
                request.Metadata.Table?.DataSourceType != DataSourceType.HttpDeclarative &&
                binding.TargetPattern is { } explicitPattern &&
                !TargetPatternMatcher.IsRegex(explicitPattern) &&
                !explicitPattern.Contains('*', StringComparison.Ordinal) &&
                !explicitPattern.Contains('?', StringComparison.Ordinal) &&
                MatchesTarget(binding, table))
            {
                RowPolicyParameterBinder.EnsureCrossSourceAllowed(filter, table.Domain);
            }

            if (request.Metadata.Table?.DataSourceType != DataSourceType.HttpDeclarative &&
                filter.EvaluationMode != RowPolicyEvaluationMode.KeySet)
            {
                return (false, Array.Empty<string>());
            }

            if (binding.TargetPattern != null && !MatchesTarget(binding, table))
            {
                return (false, Array.Empty<string>());
            }
        }
        else if (TargetPatternMatcher.IsRegex(binding.TargetPattern))
        {
            if (!MatchesTarget(binding, table))
            {
                return (false, Array.Empty<string>());
            }
        }
        else
        {
            var effectivePattern = binding.TargetPattern ?? profile.Scope;
            if (!MatchesScope(effectivePattern, table))
            {
                return (false, Array.Empty<string>());
            }
        }

        var required = VirtualFilterColumns.RequiredTargetColumns(filter, binding);
        var missing = required.Where(c => !request.Metadata.HasColumn(c)).ToList();
        return missing.Count == 0 ? (true, Array.Empty<string>()) : (false, missing);
    }

    internal static bool MatchesScope(string pattern, TableIdentifier table) =>
        TargetPatternMatcher.IsRegex(pattern)
            ? TargetPatternMatcher.IsMatch(pattern, table)
            : ObjectPattern.Parse(pattern).MatchesObject(table);

    private static bool MatchesTarget(FilterBinding binding, TableIdentifier table)
    {
        if (binding.TargetPattern == null)
        {
            return true;
        }

        try
        {
            return binding.MatchesTarget(table);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        {
            // A pattern that cannot be evaluated never means "binding does not apply": deny with the stable code (same as the mandatory filter resolver).
            throw new VirtualFilterBindingException(
                VirtualFilterErrorCodes.PatternEvaluationFailed,
                $"The target pattern of the filter '{binding.FilterName}' could not be evaluated for '{table}' ({ex.GetType().Name}).");
        }
    }

    internal static List<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)> ApplySupersedes(
        List<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)> applying)
    {
        var superseded = new HashSet<(string ProfileName, string FilterName, string? TargetPattern)>();
        foreach (var target in applying)
        {
            foreach (var candidate in applying)
            {
                if (!candidate.Filter.Supersedes.Contains(target.Filter.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                bool sameContext = string.Equals(candidate.Profile.Name, target.Profile.Name, StringComparison.Ordinal) ||
                    (candidate.Filter.ManagedBy != null && target.Filter.ManagedBy != null &&
                     candidate.Filter.ManagedBy.Equals(target.Filter.ManagedBy) &&
                     candidate.Profile.ManagedBy != null && target.Profile.ManagedBy != null &&
                     candidate.Profile.ManagedBy.Equals(target.Profile.ManagedBy));

                if (sameContext)
                {
                    superseded.Add((target.Profile.Name, target.Binding.FilterName, target.Binding.TargetPattern));
                    break;
                }
            }
        }

        return applying
            .Where(a => !superseded.Contains((a.Profile.Name, a.Binding.FilterName, a.Binding.TargetPattern)))
            .ToList();
    }
}
