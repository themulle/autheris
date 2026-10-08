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
    FilterObjectKinds ObjectKind = FilterObjectKinds.Relation);

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
    private readonly ConcurrentDictionary<string, MandatoryFilterOutcome> _memo = new(StringComparer.Ordinal);
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

        var key = string.Join('\u001f',
            snapshot.Generation, query.Tenant.Value, query.UserSid.Value,
            string.Join(',', query.GroupSids.Select(s => s.Value).Order(StringComparer.Ordinal)),
            string.Join(',', query.Roles.Order(StringComparer.Ordinal)),
            query.Metadata.Identifier.ToQualifiedName(), query.Metadata.Dialect, (int)query.ObjectKind,
            string.Join(',', query.Metadata.Columns.Select(c => c.ColumnName)));
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var outcome = Resolve(snapshot, query);
        _memo[key] = outcome;
        return outcome;
    }

    private MandatoryFilterOutcome Resolve(VirtualFilterSnapshot snapshot, MandatoryFilterQuery query)
    {
        var table = query.Metadata.Identifier;
        var profiles = snapshot.Profiles
            .Where(p => p.TenantId == query.Tenant &&
                        GranteeMatcher.Matches(p.GranteeType, p.GranteeSid, p.RoleName, null, query.UserSid, query.GroupSids, query.Roles))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var filtersByName = snapshot.Filters.Where(f => f.TenantId == query.Tenant).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var applying = new Dictionary<string, (VirtualFilter Filter, FilterBinding Binding)>(StringComparer.Ordinal);
        string? currentFilter = null;
        try
        {
            foreach (var profile in profiles)
            {
                if (!Pattern(profile.Scope).MatchesObject(table))
                {
                    continue;
                }

                bool covered = false;
                foreach (var binding in profile.Bindings)
                {
                    currentFilter = binding.FilterName;
                    if (!filtersByName.TryGetValue(binding.FilterName, out var filter))
                    {
                        throw new InvalidOperationException($"The profile '{profile.Name}' binds the unknown virtual filter '{binding.FilterName}'.");
                    }

                    if (Applies(filter, binding, profile, query))
                    {
                        covered = true;
                        applying.TryAdd(filter.Name + "|" + binding.TargetPattern, (filter, binding));
                    }
                }

                if (!covered && profile.Uncovered == UncoveredPolicy.Deny)
                {
                    return MandatoryFilterOutcome.Deny(
                        $"Virtual filters: '{table.ToQualifiedName()}' is in the scope of the profile '{profile.Name}' but no filter of the profile covers it (uncovered: deny).");
                }
            }

            if (applying.Count == 0)
            {
                return MandatoryFilterOutcome.None;
            }

            var names = applying.Values.Select(a => a.Filter.Name).ToHashSet(StringComparer.Ordinal);
            var superseded = applying.Values.SelectMany(a => a.Filter.Supersedes).Where(names.Contains).ToHashSet(StringComparer.Ordinal);
            var effective = applying.Values
                .Where(a => !superseded.Contains(a.Filter.Name))
                .OrderBy(a => a.Filter.Name, StringComparer.Ordinal)
                .ThenBy(a => a.Binding.TargetPattern, StringComparer.Ordinal)
                .ToList();

            var parts = new List<string>(effective.Count);
            foreach (var (filter, binding) in effective)
            {
                currentFilter = filter.Name;
                parts.Add(_predicates.Build(filter, binding, query.Metadata, query.Metadata.Dialect));
            }

            var predicate = parts.Count == 1 ? parts[0] : string.Join(" AND ", parts.Select(p => $"({p})"));
            return new MandatoryFilterOutcome(false, null, predicate, effective.Select(a => a.Filter.Name).Distinct(StringComparer.Ordinal).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or RegexMatchTimeoutException)
        {
            return MandatoryFilterOutcome.Deny($"Virtual filters: the filter '{currentFilter}' could not be evaluated for '{table.ToQualifiedName()}' ({ex.Message}).");
        }
    }

    private bool Applies(VirtualFilter filter, FilterBinding binding, AccessProfile profile, MandatoryFilterQuery query)
    {
        var table = query.Metadata.Identifier;
        if ((binding.ObjectKinds & query.ObjectKind) == 0 ||
            !string.Equals(table.Domain, filter.Source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pattern = Pattern(binding.TargetPattern ?? profile.Scope);
        if (!pattern.MatchesObject(table))
        {
            return false;
        }

        if (pattern.HasColumnSegment && !query.Metadata.Columns.Any(c => pattern.MatchesColumn(c.ColumnName)))
        {
            return false;
        }

        return VirtualFilterColumns.RequiredTargetColumns(filter, binding).All(query.Metadata.HasColumn);
    }

    private ObjectPattern Pattern(string text) => _patterns.GetOrAdd(text, ObjectPattern.Parse);
}
