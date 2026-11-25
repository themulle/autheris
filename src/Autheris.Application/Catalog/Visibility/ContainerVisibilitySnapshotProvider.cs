using System.Collections.Concurrent;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Products;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Catalog.Visibility;

/// <summary>Builds the <see cref="ContainerVisibilitySnapshot"/> of a caller.</summary>
public interface IContainerVisibilitySnapshotProvider
{
    /// <summary>Never throws; a failure answers <see cref="ContainerVisibilitySnapshot.Unavailable"/> (fail-closed).</summary>
    ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default);
}

/// <summary>
/// Caches snapshots per <c>(tenant, subject set, capacity flags, governance epoch)</c> with a bounded TTL (at most the governance
/// maximum, never longer than the earliest grant expiry). Degraded group resolution yields a snapshot without grants and ownership.
/// Failures are not cached.
/// </summary>
public sealed class ContainerVisibilitySnapshotProvider : IContainerVisibilitySnapshotProvider, IGovernanceCacheInvalidator, IDisposable
{
    private const int DefaultCapacity = 2048;

    private readonly record struct CacheKey(string TenantId, string SubjectKey, bool IsGovernance, bool IsItAdmin, bool GroupsComplete, long GovernanceEpoch);

    private sealed record Cached(ContainerVisibilitySnapshot Snapshot, long CachedAtTimestamp, DateTimeOffset? EarliestValidTo);

    private readonly IGovernanceDomainRepository _domains;
    private readonly ICatalogVisibilityGrantRepository _catalogGrants;
    private readonly IConsentRepository _consents;
    private readonly IGovernanceCapabilities _capabilities;
    private readonly GovernanceEpochProvider _epochProvider;
    private readonly IDataProductRepository? _products;
    private readonly int _maxContainers;
    private readonly int _maxDataGrants;
    private readonly ILogger _logger;
    private readonly TimeSpan _maxTtl;
    private readonly TimeProvider _time;
    private readonly BoundedCache<CacheKey, Cached> _cache;
    private readonly ConcurrentDictionary<string, long> _tenantGenerations = new(StringComparer.Ordinal);
    private readonly IDisposable? _changeSubscription;
    private readonly IDisposable? _epochSubscription;
    private long _globalGeneration;
    private int _disposed;

    public ContainerVisibilitySnapshotProvider(
        IGovernanceDomainRepository domains,
        ICatalogVisibilityGrantRepository catalogGrants,
        IConsentRepository consents,
        IGovernanceCapabilities capabilities,
        GovernanceEpochProvider epochProvider,
        IOptions<GovernanceCacheSettings>? cacheSettings = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null,
        IDataProductRepository? products = null,
        IOptions<AccessPolicyOptions>? accessPolicy = null,
        ILogger<ContainerVisibilitySnapshotProvider>? logger = null)
    {
        _domains = domains ?? throw new ArgumentNullException(nameof(domains));
        _catalogGrants = catalogGrants ?? throw new ArgumentNullException(nameof(catalogGrants));
        _consents = consents ?? throw new ArgumentNullException(nameof(consents));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _epochProvider = epochProvider ?? throw new ArgumentNullException(nameof(epochProvider));
        _products = products;
        _logger = logger ?? NullLogger<ContainerVisibilitySnapshotProvider>.Instance;
        var policy = accessPolicy?.Value ?? new AccessPolicyOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxPageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaxTablesPerAssignment, 1);
        _maxContainers = policy.MaxPageSize;
        _maxDataGrants = policy.MaxTablesPerAssignment;

        var settings = cacheSettings?.Value ?? new GovernanceCacheSettings();
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxTtlSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxTtlSeconds, 300);
        _maxTtl = TimeSpan.FromSeconds(settings.MaxTtlSeconds);
        _time = timeProvider ?? TimeProvider.System;
        _cache = new BoundedCache<CacheKey, Cached>(DefaultCapacity, _maxTtl, _time);

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

    public async ValueTask<ContainerVisibilitySnapshot> GetAsync(TenantId tenant, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        bool isGovernance;
        bool isItAdmin;
        try
        {
            isGovernance = _capabilities.IsGovernance(principal, tenant);
            isItAdmin = _capabilities.IsItAdmin(principal, tenant);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ContainerVisibilitySnapshot.Unavailable();
        }

        var isAppOnly = Autheris.Application.Catalog.External.AppOnlyPrincipal.Is(principal);
        if (isAppOnly)
        {
            isGovernance = false;
            isItAdmin = false;
        }

        var userSid = principal.GetUserSid();
        if (string.IsNullOrWhiteSpace(tenant.Value) || (userSid is null && !isAppOnly))
        {
            return ContainerVisibilitySnapshot.Unavailable(isGovernance, isItAdmin);
        }

        var groupsComplete = userSid is not null
            && GroupResolutionClaims.Read(principal, _time.GetUtcNow()) == GroupResolutionStatus.Complete;
        var subjects = userSid is { } sid
            ? new List<Sid>(principal.GetGroupSids()) { sid }
            : [];
        var subjectKey = userSid is null ? "app-only-unbound" : BuildSubjectKey(subjects);

        try
        {
            var tenantKey = tenant.Value;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var startGlobalGen = Volatile.Read(ref _globalGeneration);
                var startTenantGen = _tenantGenerations.GetValueOrDefault(tenantKey);
                var epoch = await _epochProvider.GetEpochAsync(tenant, ct).ConfigureAwait(false);
                var key = new CacheKey(tenantKey, subjectKey, isGovernance, isItAdmin, groupsComplete, epoch);

                if (_cache.TryGet(key, out var cached))
                {
                    if (!IsExpired(cached) && IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
                    {
                        return cached.Snapshot;
                    }

                    _cache.TryRemove(key);
                }

                var entry = await LoadAsync(tenant, subjects, groupsComplete, isGovernance, isItAdmin, ct).ConfigureAwait(false);
                if (!IsSameGeneration(tenantKey, startGlobalGen, startTenantGen))
                {
                    if (attempt == 0)
                    {
                        continue;
                    }

                    return entry.Snapshot;
                }

                _cache.Set(key, entry);
                return entry.Snapshot;
            }

            return ContainerVisibilitySnapshot.Unavailable(isGovernance, isItAdmin);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ContainerVisibilitySnapshot.Unavailable(isGovernance, isItAdmin);
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

    private async Task<Cached> LoadAsync(
        TenantId tenant,
        List<Sid> subjects,
        bool groupsComplete,
        bool isGovernance,
        bool isItAdmin,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var containers = new Dictionary<ContainerRef, ContainerInfo>();

        var domains = await _domains.ListAsync(tenant, _maxContainers, 0, ct).ConfigureAwait(false);
        if (domains.Count >= _maxContainers)
        {
            // Truncated: containers beyond the bound are absent from the snapshot and therefore invisible (fail-closed).
            _logger.LogWarning("Catalog visibility read hit the domain bound ({Bound}); further domains stay invisible.", _maxContainers);
        }

        foreach (var domain in domains)
        {
            if (domain.Tenant != tenant)
            {
                continue;
            }

            var reference = new ContainerRef(ContainerKind.Domain, domain.Id);
            containers[reference] = new ContainerInfo(
                reference,
                domain.Name,
                domain.Description,
                domain.Contact,
                domain.Visibility.NormalizeOrOnRequest(),
                domain.OwnerGroupSid,
                domain.IsSystemUnassigned);
        }

        if (_products is not null)
        {
            var products = await _products.ListProductsAsync(tenant, null, _maxContainers, ct).ConfigureAwait(false);
            if (products.Count >= _maxContainers)
            {
                _logger.LogWarning("Catalog visibility read hit the data product bound ({Bound}); further products stay invisible.", _maxContainers);
            }

            foreach (var product in products)
            {
                if (product.Tenant != tenant)
                {
                    continue;
                }

                var reference = new ContainerRef(ContainerKind.DataProduct, product.Id);
                containers[reference] = new ContainerInfo(
                    reference,
                    product.Name,
                    product.Description,
                    Contact: null,
                    product.Visibility.NormalizeOrOnRequest(),
                    product.OwnerGroupSid);
            }
        }

        var catalogGranted = new HashSet<ContainerRef>();
        var owned = new HashSet<ContainerRef>();
        var dataTables = new HashSet<TableIdentifier>();
        DateTimeOffset? earliest = null;

        // Degraded group resolution is never treated as "no groups": grants and ownership resolve to nothing.
        if (groupsComplete)
        {
            var grants = await _catalogGrants.ListActiveForSubjectsAsync(tenant, subjects, now, ct).ConfigureAwait(false);
            foreach (var grant in grants)
            {
                if (grant.TenantId != tenant || !grant.IsActive(now))
                {
                    continue;
                }

                catalogGranted.Add(new ContainerRef(grant.ContainerKind, grant.ContainerId));
                earliest = Earlier(earliest, grant.ValidTo);
            }

            var subjectSet = new HashSet<string>(subjects.Select(static s => s.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var info in containers.Values)
            {
                if (info.OwnerGroupSid is { } owner && subjectSet.Contains(owner.Value))
                {
                    owned.Add(info.Container);
                }
            }

            var consents = await _consents.GetAllActiveConsentsForSubjectsAsync(subjects, null, now, tenant, ct).ConfigureAwait(false);
            var dataGrantsTruncated = false;
            foreach (var consent in consents)
            {
                if (dataTables.Count >= _maxDataGrants)
                {
                    dataGrantsTruncated = true;
                    break;
                }

                if (consent.GrantKind == PermissionGrantKind.Data
                    && consent.Effect == ConsentEffect.Allow
                    && consent.TenantId == tenant
                    && !consent.IsPatternGrant
                    && consent.IsActive(now))
                {
                    dataTables.Add(consent.TableIdentifier);
                    earliest = Earlier(earliest, consent.ValidTo);
                }
            }

            if (dataGrantsTruncated)
            {
                _logger.LogWarning("Catalog visibility read hit the data grant bound ({Bound}); further data grants do not widen visibility.", _maxDataGrants);
            }
        }

        var snapshot = new ContainerVisibilitySnapshot(containers, catalogGranted, owned, dataTables, isGovernance, isItAdmin, groupsComplete);
        return new Cached(snapshot, _time.GetTimestamp(), earliest);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? current, DateTimeOffset? candidate) =>
        candidate is null ? current : current is null || candidate < current ? candidate : current;

    private bool IsExpired(Cached entry) =>
        _time.GetElapsedTime(entry.CachedAtTimestamp) >= _maxTtl
        || (entry.EarliestValidTo is { } validTo && _time.GetUtcNow() >= validTo);

    private bool IsSameGeneration(string tenantKey, long startGlobalGen, long startTenantGen) =>
        Volatile.Read(ref _globalGeneration) == startGlobalGen
        && _tenantGenerations.GetValueOrDefault(tenantKey) == startTenantGen;

    private static string BuildSubjectKey(List<Sid> subjects)
    {
        var values = new string[subjects.Count];
        for (var i = 0; i < subjects.Count; i++)
        {
            values[i] = subjects[i].Value ?? string.Empty;
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
