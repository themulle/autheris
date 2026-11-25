using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Services;
using Autheris.Domain.Common;

namespace Autheris.Application.Governance.Caching;

/// <summary>
/// Consolidated post-commit invalidation entry point for all governance writes (#1063 §2).
/// Coordinates <see cref="IEpochValidationService"/> (<see cref="PolicyInvalidationLog"/>),
/// catalog/profile event broadcasts, <see cref="IGovernanceChangeNotifier"/>, and local <see cref="IGovernanceCacheInvalidator"/> targets.
/// </summary>
public interface IGovernanceWriteInvalidator
{
    /// <summary>Invalidates one or more table policy epochs after a table-scoped governance write.</summary>
    Task InvalidateTablesAsync(
        TenantId? tenant,
        IEnumerable<TableIdentifier> tables,
        CatalogVersion? catalogVersion = null,
        Action<string>? metadataInvalidator = null,
        CancellationToken ct = default);

    /// <summary>Invalidates catalog-wide or tenant-wide governance caches after a cross-table governance write.</summary>
    Task InvalidateWideAsync(
        TenantId? tenant,
        CatalogVersion? catalogVersion = null,
        long governanceEpoch = 0,
        Guid? tableId = null,
        DateTimeOffset? occurredAtUtc = null,
        bool invalidateAccessProfiles = false,
        CancellationToken ct = default);

    /// <summary>Publishes a structural or catalog-content change notification when catalog rows or definitions flush.</summary>
    Task PublishCatalogChangedAsync(CatalogVersion version, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class GovernanceWriteInvalidator(
    IEpochValidationService? epochs = null,
    IGovernanceChangeNotifier? changeNotifier = null,
    Func<IEnumerable<IGovernanceCacheInvalidator>>? cacheInvalidators = null,
    IEventBus? eventBus = null,
    TimeProvider? timeProvider = null,
    Action<string>? noticeLogger = null) : IGovernanceWriteInvalidator
{
    private readonly IEpochValidationService? _epochs = epochs;
    private readonly IGovernanceChangeNotifier? _changeNotifier = changeNotifier;
    private readonly Func<IEnumerable<IGovernanceCacheInvalidator>>? _cacheInvalidators = cacheInvalidators;
    private readonly IEventBus? _eventBus = eventBus;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Action<string>? _noticeLogger = noticeLogger;

    public async Task InvalidateTablesAsync(
        TenantId? tenant,
        IEnumerable<TableIdentifier> tables,
        CatalogVersion? catalogVersion = null,
        Action<string>? metadataInvalidator = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);

        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table.TableName))
            {
                _epochs?.InvalidateAllTables();
                continue;
            }

            metadataInvalidator?.Invoke(table.ToString().ToLowerInvariant());
            if (_epochs is not null)
            {
                await _epochs.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
            }
        }

        if (catalogVersion is { } version)
        {
            await PublishCatalogChangedAsync(version, ct).ConfigureAwait(false);
        }

        var evt = tenant is { } t
            ? BuildChangeEvent(t, 0, null, _time.GetUtcNow().ToUniversalTime())
            : GovernanceChangeEvent.All(_time.GetUtcNow().ToUniversalTime());
        await BroadcastChangeAsync(evt, ct).ConfigureAwait(false);
    }

    public async Task InvalidateWideAsync(
        TenantId? tenant,
        CatalogVersion? catalogVersion = null,
        long governanceEpoch = 0,
        Guid? tableId = null,
        DateTimeOffset? occurredAtUtc = null,
        bool invalidateAccessProfiles = false,
        CancellationToken ct = default)
    {
        _epochs?.InvalidateAllTables();

        if (catalogVersion is { } version)
        {
            await PublishCatalogChangedAsync(version, ct).ConfigureAwait(false);
        }

        var evt = BuildChangeEvent(tenant, governanceEpoch, tableId, occurredAtUtc ?? _time.GetUtcNow().ToUniversalTime());

        if (invalidateAccessProfiles && _eventBus is not null && !evt.IsFullInvalidation)
        {
            try
            {
                await _eventBus.PublishAsync(AccessProfileCache.InvalidationChannel, evt.TenantId, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _noticeLogger?.Invoke("Failed to publish access profile invalidation event; polling will detect changes.");
            }
        }

        await BroadcastChangeAsync(evt, ct).ConfigureAwait(false);
    }

    public async Task PublishCatalogChangedAsync(CatalogVersion version, CancellationToken ct = default)
    {
        _epochs?.InvalidateAllTables();
        if (_eventBus is { } bus)
        {
            try
            {
                await bus.PublishAsync(
                    ICatalogVersionProvider.CatalogChangedEventChannel,
                    new CatalogChangedEvent(version.StructureVersion, version.ContentVersion, _time.GetUtcNow()),
                    ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _noticeLogger?.Invoke("Failed to publish catalog changed event; polling will detect changes.");
            }
        }
    }

    private Task NotifyTenantOrAllAsync(TenantId tenant, long governanceEpoch, Guid? tableId, DateTimeOffset? occurredAtUtc, CancellationToken ct)
    {
        var evt = BuildChangeEvent(tenant, governanceEpoch, tableId, occurredAtUtc ?? _time.GetUtcNow().ToUniversalTime());
        return BroadcastChangeAsync(evt, ct);
    }

    private static GovernanceChangeEvent BuildChangeEvent(TenantId? tenant, long epoch, Guid? tableId, DateTimeOffset occurredAtUtc)
    {
        var safeEpoch = Math.Max(0, epoch);
        if (tenant is { } t && !string.IsNullOrWhiteSpace(t.Value) && TenantId.TryParse(t.Value, out var valid))
        {
            return new GovernanceChangeEvent(valid.Value, safeEpoch, tableId, occurredAtUtc);
        }

        return new GovernanceChangeEvent(string.Empty, safeEpoch, tableId, occurredAtUtc);
    }

    private async Task BroadcastChangeAsync(GovernanceChangeEvent evt, CancellationToken ct)
    {
        if (_changeNotifier is not null)
        {
            await _changeNotifier.NotifyAsync(evt, ct).ConfigureAwait(false);
        }

        if (_cacheInvalidators is null)
        {
            return;
        }

        foreach (var target in _cacheInvalidators())
        {
            try
            {
                if (evt.IsFullInvalidation)
                {
                    target.InvalidateAll();
                }
                else
                {
                    target.InvalidateTenant(evt.TenantId);
                }
            }
            catch
            {
                target.InvalidateAll();
            }
        }
    }
}
