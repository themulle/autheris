namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.VirtualFilters.Federation;

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

    /// <summary>F-GOV-14 (M1): the effective (filter, binding) pairs behind <see cref="PredicateSql"/>, one per identity.</summary>
    public IReadOnlyList<AppliedVirtualFilterBinding> AppliedBindings { get; init; } = Array.Empty<AppliedVirtualFilterBinding>();

    /// <summary>
    /// WP-X0g: the typed form of <see cref="PredicateSql"/> (every key value a bound policy parameter), or null when a predicate
    /// builder has no typed form. The text stays for the legacy consumers until X1.
    /// </summary>
    public TrinoSqlEngine.Governance.PolicyPredicate? TypedPredicate { get; init; }

    public bool Equals(MandatoryFilterOutcome? other) =>
        other is not null && IsDenied == other.IsDenied && DenyReason == other.DenyReason &&
        PredicateSql == other.PredicateSql && TypedPredicate?.Fingerprint == other.TypedPredicate?.Fingerprint && AppliedFilters.SequenceEqual(other.AppliedFilters) &&
        AppliedBindings.SequenceEqual(other.AppliedBindings);

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

    /// <summary>
    /// WP-X0g: typed form of <see cref="Build"/>; its policy parameters are named <paramref name="parameterPrefix"/> plus a counter so
    /// that predicates of several filters merge. Null (the default) means the builder has no typed form and the resolver keeps the text only.
    /// </summary>
    TrinoSqlEngine.Governance.PolicyPredicate? BuildTyped(
        VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect, string parameterPrefix) => null;

    /// <summary>
    /// SR-L-11: <see cref="Build(VirtualFilter, FilterBinding, TableMetadata, DatabaseDialect)"/> with the caller's tenant bound to every tenanted
    /// source table. A builder that does not know tenants ignores it (the default).
    /// </summary>
    string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect, VirtualFilterTenantContext? tenant) =>
        Build(filter, binding, target, dialect);

    /// <summary>SR-L-11: tenant-bound typed form; ignores the tenant by default.</summary>
    TrinoSqlEngine.Governance.PolicyPredicate? BuildTyped(
        VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect, string parameterPrefix, VirtualFilterTenantContext? tenant) =>
        BuildTyped(filter, binding, target, dialect, parameterPrefix);
}

/// <summary>Restrictive row predicates from virtual filters (design 3.3): never grants, only narrows.</summary>
public interface IMandatoryRowFilterResolver
{
    ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default);

    /// <summary>
    /// Whether the tenant has any Virtual Filter profile. A caller without a subject identity cannot be matched to a
    /// grantee, so a channel that would otherwise substitute an anonymous identity must deny when this is true. An
    /// implementation that cannot tell answers true (fail closed).
    /// </summary>
    ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default);
}

/// <summary>Explicit "no virtual filters" (tests and tools); production wiring always uses <see cref="MandatoryRowFilterResolver"/>.</summary>
public sealed class NullMandatoryRowFilterResolver : IMandatoryRowFilterResolver
{
    public static readonly NullMandatoryRowFilterResolver Instance = new();

    public ValueTask<MandatoryFilterOutcome> ResolveAsync(MandatoryFilterQuery query, CancellationToken ct = default) =>
        ValueTask.FromResult(MandatoryFilterOutcome.None);

    public ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(false);
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
    // WP-PF-06: bounded per-user memo (5,000 users x 100 tables would otherwise grow to hundreds of MB); a miss re-resolves.
    private readonly Autheris.Application.Caching.BoundedCache<VirtualFilterMemoKey, MandatoryFilterOutcome> _memo = new(50_000);
    private readonly Autheris.Application.Caching.BoundedCache<string, ObjectPattern> _patterns = new(10_000, comparer: StringComparer.Ordinal);
    private readonly ITableMetadataRepository? _catalog;
    private readonly SqlDataSourceOptions? _dataSources;
    private long _memoGeneration = -1;

    /// <param name="snapshots">Current filters and profiles.</param>
    /// <param name="predicates">Builds the SQL of one applying filter.</param>
    /// <param name="catalog">
    /// SR-L-11: resolves the tenant column of the source tables of the filters. Production wiring always supplies it; without a
    /// catalog (tests) the predicates are built without tenant binding.
    /// </param>
    /// <param name="options">Gateway options (<c>DataSources:RequireTenantColumn</c> and its exempt tables).</param>
    public MandatoryRowFilterResolver(
        IVirtualFilterSnapshotProvider snapshots,
        IVirtualFilterPredicateBuilder predicates,
        ITableMetadataRepository? catalog = null,
        Microsoft.Extensions.Options.IOptions<GatewayOptions>? options = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _predicates = predicates ?? throw new ArgumentNullException(nameof(predicates));
        _catalog = catalog;
        _dataSources = options?.Value?.DataSources;
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
            TargetPatternMatcher.ClearCache();
        }

        var key = new VirtualFilterMemoKey(snapshot.Generation, query);
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        VirtualFilterTenantContext? tenantContext;
        try
        {
            tenantContext = await LoadTenantContextAsync(snapshot, query.Tenant, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the tenant columns of the source tables the tenant cannot be bound (fail closed, not memoized).
            return MandatoryFilterOutcome.Deny($"{VirtualFilterErrorCodes.BindingMisconfigured}: the tenant columns of the virtual filter source tables could not be resolved ({ex.GetType().Name}).");
        }

        var outcome = Resolve(snapshot, query, trace: null, tenantContext);
        _memo[key] = outcome;
        return outcome;
    }

    /// <summary>
    /// SR-L-11: the tenant column of every source table of the tenant's filters (structured FROM/JOIN tables and the tables of sql
    /// definitions), looked up once per resolution. A table that is unknown or must be tenanted and is not fails only when a filter
    /// that uses it is built (deny).
    /// </summary>
    private async ValueTask<VirtualFilterTenantContext?> LoadTenantContextAsync(VirtualFilterSnapshot snapshot, TenantId tenant, CancellationToken ct)
    {
        if (_catalog == null)
        {
            return null;
        }

        var tables = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in snapshot.Filters.Where(f => f.TenantId == tenant))
        {
            IEnumerable<TableIdentifier> sources;
            if (filter.Structured != null)
            {
                sources = filter.Structured.Joins.Select(j => j.Table).Prepend(filter.Structured.From);
            }
            else if (filter.Sql != null)
            {
                try
                {
                    sources = SqlFilterCompiler.ReferencedTables(filter);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    continue;   // building this filter fails closed on its own
                }
            }
            else
            {
                continue;
            }

            foreach (var source in sources)
            {
                tables[source.ToString()] = source;
            }
        }

        var columns = new Dictionary<string, (string? Column, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, identifier) in tables)
        {
            var metadata = await _catalog.GetTableMetadataAsync(identifier, ct).ConfigureAwait(false);
            if (metadata == null)
            {
                columns[name] = (null, $"the source table '{name}' is unknown in the catalog");
                continue;
            }

            try
            {
                columns[name] = (VirtualFilterTenantColumns.Resolve(metadata, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables), null);
            }
            catch (InvalidOperationException ex)
            {
                columns[name] = (null, ex.Message);
            }
        }

        return new VirtualFilterTenantContext(tenant.Value, identifier =>
        {
            if (!columns.TryGetValue(identifier.ToString(), out var entry))
            {
                throw new InvalidOperationException($"The source table '{identifier}' is unknown in the catalog.");
            }

            return entry.Error != null ? throw new InvalidOperationException(entry.Error) : entry.Column;
        });
    }

    public async ValueTask<bool> HasActiveProfilesAsync(TenantId tenant, CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
            return snapshot.Profiles.Any(p => p.TenantId == tenant && p.Status == FilterApprovalStatus.Active);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;   // without the current profiles nothing can be ruled out (fail closed)
        }
    }

    /// <summary>Same resolution as <see cref="ResolveAsync"/>, with the reason for every profile and binding (no memo).</summary>
    public async ValueTask<MandatoryFilterExplanation> ExplainAsync(MandatoryFilterQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await _snapshots.GetAsync(ct).ConfigureAwait(false);
        var trace = new List<ProfileExplanation>();
        var tenantContext = await LoadTenantContextAsync(snapshot, query.Tenant, ct).ConfigureAwait(false);
        var outcome = Resolve(snapshot, query, trace, tenantContext);
        return new MandatoryFilterExplanation(snapshot.Generation, outcome, trace);
    }

    private MandatoryFilterOutcome Resolve(VirtualFilterSnapshot snapshot, MandatoryFilterQuery query, List<ProfileExplanation>? trace, VirtualFilterTenantContext? tenantContext)
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
                        // A binding that matched the object but lacks catalog columns is a misconfiguration (ST-37), not "uncovered".
                        uncoveredDeny = MandatoryFilterOutcome.Deny(missingBinding != null
                            ? $"{VirtualFilterErrorCodes.BindingMisconfigured}: '{table.ToString()}' matches filter '{missingBinding.Value.Filter.Name}' in profile '{profile.Name}', but lacks required column(s): {string.Join(", ", missingBinding.Value.Missing)} (uncovered: deny, fail-closed)."
                            : $"Virtual filters: '{table.ToString()}' is in the scope of the profile '{profile.Name}' but no filter of the profile covers it (uncovered: deny).");
                        if (trace == null)
                        {
                            return uncoveredDeny;
                        }
                    }
                    else if (missingBinding != null)
                    {
                        // SR15-13: Matching binding lacks required columns, and profile is not covered by another filter -> fail-closed (Deny)
                        uncoveredDeny = MandatoryFilterOutcome.Deny(
                            $"{VirtualFilterErrorCodes.BindingMisconfigured}: '{table.ToString()}' matches filter '{missingBinding.Value.Filter.Name}' in profile '{profile.Name}', but lacks required column(s): {string.Join(", ", missingBinding.Value.Missing)} (uncovered: deny, fail-closed).");
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
            var typedParts = new List<TrinoSqlEngine.Governance.PolicyPredicate>(effective.Count);
            bool typedComplete = true;
            var distinctPredicates = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < effective.Count; i++)
            {
                var (filter, binding, _) = effective[i];
                currentFilter = filter.Name;
                var pred = tenantContext == null
                    ? _predicates.Build(filter, binding, query.Metadata, query.Metadata.Dialect)
                    : _predicates.Build(filter, binding, query.Metadata, query.Metadata.Dialect, tenantContext);

                // The prefix carries the position in the effective order, so parameter names of different filters never collide;
                // an exception here denies (fail closed), it never falls back to the text.
                var typedPrefix = $"{SqlFilterCompiler.DefaultTypedParameterPrefix}{i}_";
                var typed = tenantContext == null
                    ? _predicates.BuildTyped(filter, binding, query.Metadata, query.Metadata.Dialect, typedPrefix)
                    : _predicates.BuildTyped(filter, binding, query.Metadata, query.Metadata.Dialect, typedPrefix, tenantContext);
                if (distinctPredicates.Add(pred))
                {
                    parts.Add(pred);
                    if (typed == null)
                    {
                        typedComplete = false;
                    }
                    else
                    {
                        typedParts.Add(typed);
                    }
                }
            }

            var predicate = parts.Count == 1 ? parts[0] : string.Join(" AND ", parts.Select(p => $"({p})"));
            var appliedBindings = effective
                .Select(a => new AppliedVirtualFilterBinding(a.Filter, a.Binding, snapshot.Generation))
                .Distinct()
                .ToList();
            return new MandatoryFilterOutcome(false, null, predicate, effective.Select(a => a.Filter.Name).Distinct(StringComparer.Ordinal).ToList())
            {
                AppliedBindings = appliedBindings,
                TypedPredicate = typedComplete && typedParts.Count > 0 ? typedParts.Aggregate((left, right) => left.And(right)) : null
            };
        }
        catch (TargetPatternEvaluationException ex)
        {
            // SEC-7/SEC-8 (F-GOV-14): a pattern that cannot be evaluated never means "binding does not apply".
            return MandatoryFilterOutcome.Deny(
                $"{VirtualFilterErrorCodes.PatternEvaluationFailed}: the target pattern of the filter '{currentFilter}' could not be evaluated for '{table.ToString()}' ({ex.InnerException?.GetType().Name}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        {
            return MandatoryFilterOutcome.Deny($"{VirtualFilterErrorCodes.BindingMisconfigured}: the filter '{currentFilter}' could not be evaluated for '{table.ToString()}' ({ex.Message}).");
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

        bool isCrossSource = !string.Equals(table.Domain, filter.Source, StringComparison.OrdinalIgnoreCase);
        if (isCrossSource)
        {
            if (query.Metadata.Table?.DataSourceType != DataSourceType.HttpDeclarative ||
                binding.TargetPattern == null ||
                !MatchesTarget(binding, table))
            {
                return (false, $"object is not in the filter's data source '{filter.Source}'", []);
            }
        }
        else if (TargetPatternMatcher.IsRegex(binding.TargetPattern))
        {
            // A regex pattern means the same in both branches: full match on domain.schema.table.
            if (!MatchesTarget(binding, table))
            {
                return (false, "pattern does not match the object", []);
            }
        }
        else
        {
            var pattern = Pattern(binding.TargetPattern ?? profile.Scope);
            if (!pattern.MatchesObject(table))
            {
                return (false, "pattern does not match the object", []);
            }

            if (pattern.HasColumnSegment && !query.Metadata.Columns.Any(c => pattern.MatchesColumn(c.ColumnName)))
            {
                return (false, "pattern matches no column of the object", []);
            }
        }

        var missing = VirtualFilterColumns.RequiredTargetColumns(filter, binding).Where(c => !query.Metadata.HasColumn(c)).ToList();
        return missing.Count == 0 ? (true, null, []) : (false, "object lacks columns the filter needs", missing);
    }

    private ObjectPattern Pattern(string text) => _patterns.GetOrAdd(text, ObjectPattern.Parse);

    private static bool MatchesTarget(FilterBinding binding, TableIdentifier table)
    {
        try
        {
            return binding.MatchesTarget(table);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        {
            throw new TargetPatternEvaluationException(ex);
        }
    }

    /// <summary>Carries a target pattern failure to the resolver boundary, where it becomes a deny with its code.</summary>
    private sealed class TargetPatternEvaluationException(Exception inner)
        : Exception("The target pattern could not be evaluated.", inner);
}
