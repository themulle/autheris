namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;

/// <summary>One caller and one object for the virtual filter resolution.</summary>
public sealed record MandatoryFilterQuery(
    Sid UserSid,
    IReadOnlySet<Sid> GroupSids,
    IReadOnlySet<string> Roles,
    TenantId Tenant,
    TableMetadata Metadata,
    FilterObjectKinds ObjectKind = FilterObjectKinds.Relation,
    IReadOnlySet<Sid>? AllUserSids = null);

/// <summary>
/// Result of the virtual filter resolution: nothing, a deny (object uncovered, or a filter could not be evaluated),
/// or a predicate (AND of the applying filters, in the target dialect, over the alias <c>autheris_target</c>).
/// </summary>
public sealed record MandatoryFilterOutcome(bool IsDenied, string? DenyReason, string? PredicateSql, IReadOnlyList<string> AppliedFilters)
{
    public static readonly MandatoryFilterOutcome None = new(false, null, null, Array.Empty<string>());

    public static MandatoryFilterOutcome Deny(string reason) => new(true, reason, null, Array.Empty<string>());

    public bool Equals(MandatoryFilterOutcome? other) =>
        other is not null && IsDenied == other.IsDenied && DenyReason == other.DenyReason &&
        PredicateSql == other.PredicateSql && AppliedFilters.SequenceEqual(other.AppliedFilters);

    public override int GetHashCode() => HashCode.Combine(IsDenied, DenyReason, PredicateSql);
}

/// <summary>Why one binding of a profile applies to an object or not (phase 6, effective-filters).</summary>
public sealed record BindingExplanation(
    string Filter,
    string Pattern,
    bool Applies,
    string? Reason,
    IReadOnlyList<string> MissingColumns,
    string? SupersededBy);

/// <summary>One profile of the caller and its effect on the object.</summary>
public sealed record ProfileExplanation(string Profile, string Scope, bool InScope, UncoveredPolicy? Uncovered, IReadOnlyList<BindingExplanation> Bindings);

/// <summary>Full explanation of the virtual filter resolution for one caller and one object.</summary>
public sealed record MandatoryFilterExplanation(long Generation, MandatoryFilterOutcome Outcome, IReadOnlyList<ProfileExplanation> Profiles)
{
    /// <summary>allow (no or applying filters), deny (an evaluation error) or unmatched-deny (uncovered object).</summary>
    public string Decision => !Outcome.IsDenied ? "allow" : Outcome.DenyReason?.Contains("uncovered", StringComparison.Ordinal) == true ? "unmatched-deny" : "deny";
}

/// <summary>Current virtual filters and profiles; reloads when the generation changes.</summary>
public interface IVirtualFilterSnapshotProvider
{
    ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>Forces the next <see cref="GetAsync"/> to compare the generation (after a local write).</summary>
    void Invalidate();
}

/// <summary>SQL of one applying filter for one object (dialect of the object's data source, alias <c>autheris_target</c>).</summary>
public interface IVirtualFilterPredicateBuilder
{
    string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect);
}

/// <summary>Restrictive row predicates from virtual filters (design 3.3): never grants, only narrows.</summary>
public interface IMandatoryRowFilterResolver
{
    ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default);
}

/// <summary>Explicit "no virtual filters" (tests and tools); production wiring always uses <see cref="MandatoryRowFilterResolver"/>.</summary>
public sealed class NullMandatoryRowFilterResolver : IMandatoryRowFilterResolver
{
    public static readonly NullMandatoryRowFilterResolver Instance = new();

    public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default) =>
        ValueTask.FromResult(MandatoryFilterOutcome.None);
}

/// <summary>Columns a filter needs on the protected object.</summary>
public static class VirtualFilterColumns
{
    /// <summary>
    /// The key columns (renamed by the binding's column map) and, when the filter has a validity window, the binding's
    /// time column. A window without a time column cannot be enforced and is rejected (fail closed).
    /// </summary>
    public static IReadOnlyList<string> RequiredTargetColumns(VirtualFilter filter, FilterBinding binding)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(binding);

        if (filter.Sql != null)
        {
            if (binding.ColumnMap is { Count: > 0 } || binding.TimeColumn != null)
            {
                throw new InvalidOperationException($"The virtual filter '{filter.Name}' is defined in sql; a column map or time column does not apply to it.");
            }

            return filter.SqlTargetColumns;
        }

        var columns = filter.TargetKeyColumns
            .Select(key => binding.ColumnMap != null && binding.ColumnMap.TryGetValue(key, out var mapped) ? mapped : key)
            .ToList();

        if (filter.ValidFromColumn != null || filter.ValidToColumn != null)
        {
            if (binding.TimeColumn == null)
            {
                throw new InvalidOperationException($"The virtual filter '{filter.Name}' has a validity window; its binding needs a time column.");
            }

            columns.Add(binding.TimeColumn);
        }

        return columns;
    }
}

/// <summary>
/// Virtual filters, phase 3: resolves the restrictive predicate for a caller and an object from the caller's access
/// profiles. A binding applies when its pattern (or the profile scope) matches the object, the object kind is
/// included, the object lies in the filter's data source and has every column the filter needs. Applying filters are
/// combined with AND (sorted by name, order of configuration has no effect); <c>supersedes</c> removes a replaced
/// filter where both apply. An object in a profile's scope that no binding covers follows the profile's uncovered
/// policy. Any evaluation error denies (fail closed). Results are memoized per generation.
/// </summary>
public sealed class MandatoryRowFilterResolver : IMandatoryRowFilterResolver
{
    private readonly IVirtualFilterSnapshotProvider _snapshots;
    private readonly IVirtualFilterPredicateBuilder _predicates;
    private readonly ConcurrentDictionary<VirtualFilterMemoKey, MandatoryFilterOutcome> _memo = new();
    private readonly ConcurrentDictionary<string, ObjectPattern> _patterns = new(StringComparer.Ordinal);
    private long _memoGeneration = -1;

    public MandatoryRowFilterResolver(IVirtualFilterSnapshotProvider snapshots, IVirtualFilterPredicateBuilder predicates)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _predicates = predicates ?? throw new ArgumentNullException(nameof(predicates));
    }

    public async ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        VirtualFilterSnapshot snapshot;
        try
        {
            snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the current filters nothing can be decided safely.
            return MandatoryFilterOutcome.Deny($"Virtual filters could not be loaded ({ex.GetType().Name}).");
        }

        if (snapshot.Profiles.Count == 0)
        {
            return MandatoryFilterOutcome.None;
        }

        if (Interlocked.Exchange(ref _memoGeneration, snapshot.Generation) != snapshot.Generation)
        {
            _memo.Clear();
        }

        var key = new VirtualFilterMemoKey(snapshot.Generation, query);
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var outcome = Resolve(snapshot, query, trace: null);
        _memo[key] = outcome;
        return outcome;
    }

    /// <summary>Same resolution as <see cref="ResolveAsync"/>, with the reason for every profile and binding (no memo).</summary>
    public async ValueTask<MandatoryFilterExplanation> ExplainAsync(MandatoryFilterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
        var trace = new List<ProfileExplanation>();
        var outcome = Resolve(snapshot, query, trace);
        return new MandatoryFilterExplanation(snapshot.Generation, outcome, trace);
    }

    private MandatoryFilterOutcome Resolve(VirtualFilterSnapshot snapshot, MandatoryFilterQuery query, List<ProfileExplanation>? trace)
    {
        var table = query.Metadata.Identifier;
        var profiles = snapshot.Profiles
            .Where(p => p.TenantId == query.Tenant &&
                        p.Status == FilterApprovalStatus.Active &&
                        GranteeMatcher.Matches(p.GranteeType, p.GranteeSid, p.RoleName, null, query.UserSid, query.GroupSids, query.Roles, query.AllUserSids))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var filtersByName = snapshot.Filters.Where(f => f.TenantId == query.Tenant).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var applying = new List<(VirtualFilter Filter, FilterBinding Binding, VirtualFilterAccessProfile Profile)>();
        string? currentFilter = null;
        try
        {
            var explained = new List<(VirtualFilterAccessProfile Profile, List<(VirtualFilter? Filter, FilterBinding Binding, string? Reason, IReadOnlyList<string> Missing)> Bindings)>();
            MandatoryFilterOutcome? uncoveredDeny = null;
            foreach (var profile in profiles)
            {
                if (!Pattern(profile.Scope).MatchesObject(table))
                {
                    trace?.Add(new ProfileExplanation(profile.Name, profile.Scope, InScope: false, profile.Uncovered, []));
                    continue;
                }

                bool covered = false;
                (VirtualFilter Filter, IReadOnlyList<string> Missing)? missingBinding = null;
                var bindingTrace = new List<(VirtualFilter? Filter, FilterBinding Binding, string? Reason, IReadOnlyList<string> Missing)>();
                foreach (var binding in profile.Bindings)
                {
                    currentFilter = binding.FilterName;
                    if (!filtersByName.TryGetValue(binding.FilterName, out var filter))
                    {
                        throw new InvalidOperationException($"The profile '{profile.Name}' binds the unknown virtual filter '{binding.FilterName}'.");
                    }

                    var (applies, reason, missing) = Check(filter, binding, profile, query);
                    bindingTrace.Add((filter, binding, reason, missing));
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

                explained.Add((profile, bindingTrace));
                if (!covered && uncoveredDeny == null)
                {
                    if (profile.Uncovered == UncoveredPolicy.Deny)
                    {
                        uncoveredDeny = MandatoryFilterOutcome.Deny(
                            $"Virtual filters: '{table.ToString()}' is in the scope of the profile '{profile.Name}' but no filter of the profile covers it (uncovered: deny).");
                        if (trace == null)
                        {
                            return uncoveredDeny;
                        }
                    }
                    else if (missingBinding != null)
                    {
                        // SR15-13: Matching binding lacks required columns, and profile is not covered by another filter -> fail-closed (Deny)
                        uncoveredDeny = MandatoryFilterOutcome.Deny(
                            $"Virtual filters: '{table.ToString()}' matches filter '{missingBinding.Value.Filter.Name}' in profile '{profile.Name}', but lacks required column(s): {string.Join(", ", missingBinding.Value.Missing)} (uncovered: deny, fail-closed).");
                        if (trace == null)
                        {
                            return uncoveredDeny;
                        }
                    }
                }
            }

            // SG-14 / SR15-12: supersedes must be decided strictly per (Profile, Binding) context.
            // A filter may only supersede another filter in the SAME profile or when sharing identical non-null ManagedBy contexts.
            var supersededByMap = new Dictionary<(string ProfileName, string FilterName, string? TargetPattern), string>();
            foreach (var target in applying)
            {
                foreach (var candidate in applying)
                {
                    if (candidate.Filter.Supersedes.Contains(target.Filter.Name, StringComparer.Ordinal))
                    {
                        bool sameContext = string.Equals(candidate.Profile.Name, target.Profile.Name, StringComparison.Ordinal) ||
                                           (candidate.Filter.ManagedBy != null && target.Filter.ManagedBy != null && candidate.Filter.ManagedBy == target.Filter.ManagedBy &&
                                            candidate.Profile.ManagedBy != null && target.Profile.ManagedBy != null && candidate.Profile.ManagedBy == target.Profile.ManagedBy);

                        if (sameContext)
                        {
                            supersededByMap.TryAdd((target.Profile.Name, target.Binding.FilterName, target.Binding.TargetPattern), candidate.Filter.Name);
                            break;
                        }
                    }
                }
            }

            if (trace != null)
            {
                foreach (var (profile, bindings) in explained)
                {
                    trace.Add(new ProfileExplanation(profile.Name, profile.Scope, InScope: true, profile.Uncovered, bindings.Select(b =>
                    {
                        var isSuperseded = supersededByMap.TryGetValue((profile.Name, b.Binding.FilterName, b.Binding.TargetPattern), out var by);
                        return new BindingExplanation(
                            b.Binding.FilterName,
                            b.Binding.TargetPattern ?? profile.Scope,
                            Applies: b.Reason == null && !isSuperseded,
                            b.Reason,
                            b.Missing,
                            b.Reason == null && isSuperseded ? by : null);
                    }).ToList()));
                }
            }

            if (uncoveredDeny != null)
            {
                return uncoveredDeny;
            }

            if (applying.Count == 0)
            {
                return MandatoryFilterOutcome.None;
            }

            var effective = applying
                .Where(a => !supersededByMap.ContainsKey((a.Profile.Name, a.Binding.FilterName, a.Binding.TargetPattern)))
                .OrderBy(a => a.Filter.Name, StringComparer.Ordinal)
                .ThenBy(a => a.Binding.TargetPattern, StringComparer.Ordinal)
                .ThenBy(a => a.Profile.Name, StringComparer.Ordinal)
                .ToList();

            var parts = new List<string>(effective.Count);
            var distinctPredicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (filter, binding, _) in effective)
            {
                currentFilter = filter.Name;
                var pred = _predicates.Build(filter, binding, query.Metadata, query.Metadata.Dialect);
                if (distinctPredicates.Add(pred))
                {
                    parts.Add(pred);
                }
            }

            var predicate = parts.Count == 1 ? parts[0] : string.Join(" AND ", parts.Select(p => $"({p})"));
            return new MandatoryFilterOutcome(false, null, predicate, effective.Select(a => a.Filter.Name).Distinct(StringComparer.Ordinal).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        {
            return MandatoryFilterOutcome.Deny($"Virtual filters: the filter '{currentFilter}' could not be evaluated for '{table.ToString()}' ({ex.Message}).");
        }
    }

    /// <summary>Whether the binding applies; otherwise the reason and the missing columns.</summary>
    private (bool Applies, string? Reason, IReadOnlyList<string> Missing) Check(VirtualFilter filter, FilterBinding binding, VirtualFilterAccessProfile profile, MandatoryFilterQuery query)
    {
        if (filter.Status != FilterApprovalStatus.Active)
        {
            return (false, $"filter '{filter.Name}' is not active (status: {filter.Status})", []);
        }

        var table = query.Metadata.Identifier;
        if ((binding.ObjectKinds & query.ObjectKind) == 0)
        {
            return (false, $"object kind {query.ObjectKind} not bound", []);
        }

        if (!string.Equals(table.Domain, filter.Source, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"object is not in the filter's data source '{filter.Source}'", []);
        }

        var pattern = Pattern(binding.TargetPattern ?? profile.Scope);
        if (!pattern.MatchesObject(table))
        {
            return (false, "pattern does not match the object", []);
        }

        if (pattern.HasColumnSegment && !query.Metadata.Columns.Any(c => pattern.MatchesColumn(c.ColumnName)))
        {
            return (false, "pattern matches no column of the object", []);
        }

        var missing = VirtualFilterColumns.RequiredTargetColumns(filter, binding).Where(c => !query.Metadata.HasColumn(c)).ToList();
        return missing.Count == 0 ? (true, null, []) : (false, "object lacks columns the filter needs", missing);
    }

    private ObjectPattern Pattern(string text) => _patterns.GetOrAdd(text, ObjectPattern.Parse);
}
