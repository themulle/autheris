namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

/// <summary>Bounded, invalidation-stamped cross-request cache over <see cref="IConsentRepository.GetTablesWithConsentsForTenantAsync"/>.</summary>
public interface ITenantConsentTablesProvider
{
    ValueTask<IReadOnlySet<TableIdentifier>> GetTablesWithConsentsForTenantAsync(
        IConsentRepository consentRepository, TenantId tenant, CancellationToken ct = default);
}

public sealed class TenantConsentTablesProvider : ITenantConsentTablesProvider, IDisposable
{
    private const int MaxCachedTenants = 1024;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    private readonly IEpochValidationService? _epochs;
    private readonly GovernanceEpochProvider? _governanceEpochs;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedEntry(
        IConsentRepository Repository, long Generation, long ClusterSequence, long GovernanceEpoch,
        DateTimeOffset ExpiresAt, IReadOnlySet<TableIdentifier> Tables);

    private readonly record struct Stamp(long Generation, long ClusterSequence, long GovernanceEpoch);

    public TenantConsentTablesProvider(
        IEpochValidationService? epochs = null,
        GovernanceEpochProvider? governanceEpochs = null,
        IGovernanceChangeNotifier? changeNotifier = null,
        TimeProvider? timeProvider = null)
    {
        _epochs = epochs;
        _governanceEpochs = governanceEpochs;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _subscription = changeNotifier?.Subscribe(evt =>
        {
            if (evt.IsFullInvalidation) _cache.Clear();
            else _cache.TryRemove(evt.TenantId, out _);
        });
    }

    public async ValueTask<IReadOnlySet<TableIdentifier>> GetTablesWithConsentsForTenantAsync(
        IConsentRepository consentRepository, TenantId tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consentRepository);

        var stampBefore = await ReadStampAsync(tenant, ct).ConfigureAwait(false);
        if (stampBefore is not { } validBefore)
        {
            var uncached = await consentRepository.GetTablesWithConsentsForTenantAsync(tenant, ct).ConfigureAwait(false);
            return uncached as IReadOnlySet<TableIdentifier> ?? uncached.ToHashSet();
        }

        if (_cache.TryGetValue(tenant.Value, out var entry) &&
            ReferenceEquals(entry.Repository, consentRepository) &&
            entry.Generation == validBefore.Generation &&
            entry.ClusterSequence == validBefore.ClusterSequence &&
            entry.GovernanceEpoch == validBefore.GovernanceEpoch &&
            _timeProvider.GetUtcNow() < entry.ExpiresAt)
        {
            return entry.Tables;
        }

        var loaded = await consentRepository.GetTablesWithConsentsForTenantAsync(tenant, ct).ConfigureAwait(false);
        var tableSet = loaded as IReadOnlySet<TableIdentifier> ?? loaded.ToFrozenSet();

        if (await ReadStampAsync(tenant, ct).ConfigureAwait(false) is { } validAfter && validAfter == validBefore)
        {
            if (_cache.Count >= MaxCachedTenants && !_cache.ContainsKey(tenant.Value)) _cache.Clear();
            _cache[tenant.Value] = new CachedEntry(
                consentRepository, validBefore.Generation, validBefore.ClusterSequence, validBefore.GovernanceEpoch,
                _timeProvider.GetUtcNow() + DefaultTtl, tableSet);
        }

        return tableSet;
    }

    private async ValueTask<Stamp?> ReadStampAsync(TenantId tenant, CancellationToken ct)
    {
        if (_epochs?.InvalidationGeneration is not { } generation) return null;
        var govEpoch = _governanceEpochs is null ? 0L : await _governanceEpochs.GetEpochAsync(tenant, ct).ConfigureAwait(false);
        return await _epochs.GetClusterInvalidationSequenceAsync(ct).ConfigureAwait(false) is { } cluster
            ? new Stamp(generation, cluster, govEpoch)
            : null;
    }

    public void Dispose() => _subscription?.Dispose();
}
