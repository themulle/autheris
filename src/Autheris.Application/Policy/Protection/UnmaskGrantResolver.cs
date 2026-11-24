using System.Collections.Concurrent;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Policy.Protection;

/// <summary>
/// Columns of one table the caller may read in clear form through an active UNMASK grant. An unmask grant never confers
/// row or column read access; it only changes the form of columns the caller may already read.
/// </summary>
public sealed class UnmaskCoverage
{
    public static UnmaskCoverage None { get; } = new(wholeTable: false, columns: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public UnmaskCoverage(bool wholeTable, IReadOnlySet<string> columns)
    {
        WholeTable = wholeTable;
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }

    /// <summary>True when a table-scoped unmask grant covers every column of the table.</summary>
    public bool WholeTable { get; }

    /// <summary>Columns covered by column-scoped unmask grants (case-insensitive).</summary>
    public IReadOnlySet<string> Columns { get; }

    public bool IsEmpty => !WholeTable && Columns.Count == 0;

    public bool Covers(string columnName) =>
        WholeTable || (!string.IsNullOrWhiteSpace(columnName) && Columns.Contains(columnName));
}

/// <summary>Resolves the active UNMASK grants of a caller (user and current groups) for a table.</summary>
public interface IUnmaskGrantResolver
{
    /// <summary>
    /// Returns the unmask coverage. Fail-closed: a resolver failure or incomplete group resolution yields
    /// <see cref="UnmaskCoverage.None"/>, so the columns stay masked.
    /// </summary>
    ValueTask<UnmaskCoverage> ResolveAsync(
        TenantId tenant,
        IReadOnlyCollection<Sid> subjects,
        bool groupsComplete,
        TableIdentifier table,
        CancellationToken ct = default);
}

/// <summary>
/// Reads UNMASK consents through <see cref="IConsentRepository"/> and caches them per
/// <c>(tenant, table, subject set, governance epoch)</c> with a bounded positive and negative TTL. The cache is invalidated
/// with the other governance caches (change notifications and epoch advances).
/// </summary>
public sealed class UnmaskGrantResolver : IUnmaskGrantResolver, IGovernanceCacheInvalidator, IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly record struct CacheKey(string TenantId, TableIdentifier Table, string SubjectKey, long GovernanceEpoch);

    private sealed record CachedCoverage(UnmaskCoverage Coverage, long CachedAtTimestamp, DateTimeOffset? EarliestValidTo);

    private readonly IConsentRepository _consents;
    private readonly GovernanceEpochProvider _epochProvider;
    private readonly TimeSpan _maxTtl;
    private readonly TimeSpan _negativeTtl;
    private readonly TimeProvider _time;
    private readonly BoundedCache<CacheKey, CachedCoverage> _cache;
    private readonly ConcurrentDictionary<string, long> _tenantGenerations = new(StringComparer.Ordinal);
    private readonly IDisposable? _changeSubscription;
    private readonly IDisposable? _epochSubscription;
    private long _globalGeneration;
    private int _disposed;

    public UnmaskGrantResolver(
        IConsentRepository consents,
        GovernanceEpochProvider epochProvider,
        IOptions<GovernanceCacheSettings>? cacheSettings = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null)
    {
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _epochProvider = epochProvider ?? throw new ArgumentNullException(nameof(epochProvider));

        var settings = cacheSettings?.Value ?? new GovernanceCacheSettings();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxTtlSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxTtlSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.NegativeTtlSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.NegativeTtlSeconds, 60);

        _maxTtl = TimeSpan.FromSeconds(settings.MaxTtlSeconds);
        _negativeTtl = TimeSpan.FromSeconds(Math.Min(settings.NegativeTtlSeconds, settings.MaxTtlSeconds));
        _time = timeProvider ?? TimeProvider.System;
        _cache = new BoundedCache<CacheKey, CachedCoverage>(DefaultCapacity, _maxTtl, _time);

        _changeSubscription = changeNotifier?.Subscribe(OnChange);
        _epochSubscription = _epochProvider.OnTenantEpochAdvanced(tenantId =>
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                InvalidateAll();
            }
            else
            {
                InvalidateTenant(tenantId);
            }
        });
    }

    public async ValueTask<UnmaskCoverage> ResolveAsync(
        TenantId tenant,
        IReadOnlyCollection<Sid> subjects,
        bool groupsComplete,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        if (!groupsComplete || subjects is null || subjects.Count == 0 || string.IsNullOrWhiteSpace(tenant.Value))
        {
            return UnmaskCoverage.None;
        }

        try
        {
            var tenantKey = tenant.Value;
            var subjectKey = BuildSubjectKey(subjects);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var startGlobalGen = Volatile.Read(ref _globalGeneration);
                var startTenantGen = _tenantGenerations.GetValueOrDefault(tenantKey);
                var epoch = await _epochProvider.GetEpochAsync(tenant, ct).ConfigureAwait(false);
                var key = new CacheKey(tenantKey, table, subjectKey, epoch);

                if (_cache.TryGet(key, out var cached))
                {
                    if (!IsExpired(cached) && IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
                    {
                        return cached.Coverage;
                    }

                    _cache.TryRemove(key);
                }

                var entry = await LoadAsync(tenant, subjects, table, ct).ConfigureAwait(false);
                if (!IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
                {
                    if (attempt == 0)
                    {
                        continue;
                    }

                    return entry.Coverage;
                }

                _cache.Set(key, entry);
                return entry.Coverage;
            }

            return UnmaskCoverage.None;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return UnmaskCoverage.None;
        }
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            InvalidateAll();
            return;
        }

        _tenantGenerations.AddOrUpdate(tenantId, 1, static (_, g) => g + 1);
        foreach (var key in _cache.Keys)
        {
            if (string.Equals(key.TenantId, tenantId, StringComparison.Ordinal))
            {
                _cache.TryRemove(key);
            }
        }
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _globalGeneration);
        _cache.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changeSubscription?.Dispose();
        _epochSubscription?.Dispose();
    }

    private async Task<CachedCoverage> LoadAsync(
        TenantId tenant,
        IReadOnlyCollection<Sid> subjects,
        TableIdentifier table,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var grants = await _consents.GetActiveUnmaskGrantsForSubjectsAsync(subjects, table, now, tenant, ct).ConfigureAwait(false);

        var wholeTable = false;
        HashSet<string>? columns = null;
        DateTimeOffset? earliestValidTo = null;
        foreach (var grant in grants)
        {
            if (grant.GrantKind != PermissionGrantKind.Unmask
                || grant.Effect != ConsentEffect.Allow
                || !grant.IsActive(now)
                || !string.Equals(grant.TenantId.Value, tenant.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (grant.TargetColumns is { Count: > 0 } targets)
            {
                columns ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in targets)
                {
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        columns.Add(target);
                    }
                }
            }
            else
            {
                wholeTable = true;
            }

            if (grant.ValidTo is { } validTo && (earliestValidTo is null || validTo < earliestValidTo))
            {
                earliestValidTo = validTo;
            }
        }

        var coverage = !wholeTable && columns is null
            ? UnmaskCoverage.None
            : new UnmaskCoverage(wholeTable, columns ?? (IReadOnlySet<string>)UnmaskCoverage.None.Columns);
        return new CachedCoverage(coverage, _time.GetTimestamp(), earliestValidTo);
    }

    private bool IsExpired(CachedCoverage entry)
    {
        var ttl = entry.Coverage.IsEmpty ? _negativeTtl : _maxTtl;
        if (_time.GetElapsedTime(entry.CachedAtTimestamp) >= ttl)
        {
            return true;
        }

        return entry.EarliestValidTo is { } validTo && _time.GetUtcNow() >= validTo;
    }

    private bool IsSameGeneration(string tenantKey, long startGlobalGen, long startTenantGen) =>
        Volatile.Read(ref _globalGeneration) == startGlobalGen
        && _tenantGenerations.GetValueOrDefault(tenantKey) == startTenantGen;

    private static string BuildSubjectKey(IReadOnlyCollection<Sid> subjects)
    {
        var values = new string[subjects.Count];
        var i = 0;
        foreach (var subject in subjects)
        {
            values[i++] = subject.Value ?? string.Empty;
        }

        Array.Sort(values, StringComparer.OrdinalIgnoreCase);
        return string.Join('|', values);
    }

    private void OnChange(GovernanceChangeEvent change)
    {
        if (change.IsFullInvalidation)
        {
            InvalidateAll();
        }
        else
        {
            InvalidateTenant(change.TenantId);
        }
    }
}
