namespace Autheris.Application.ResourceGroups;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ResourceGroupManager : IResourceGroupManager, IDisposable
{
    private readonly ILogger<ResourceGroupManager> _logger;
    private readonly Dictionary<ResourceGroupTier, TierState> _tiers;
    private readonly bool _enabled;
    private bool _disposed;

    public ResourceGroupManager(IOptions<GatewayOptions> options, ILogger<ResourceGroupManager> logger)
    {
        _logger = logger;
        var rgOpts = options.Value.ResourceGroups ?? new ResourceGroupsOptions();
        _enabled = rgOpts.Enabled;

        // SEC H-07: in addition to the global tier limit every tenant gets only a share of the tier.
        var tenantPercent = Math.Clamp(rgOpts.MaxConcurrentPerTenantPercent, 1, 100);
        var tenantAbsolute = Math.Max(0, rgOpts.MaxConcurrentPerTenant);

        _tiers = new Dictionary<ResourceGroupTier, TierState>
        {
            [ResourceGroupTier.Interactive] = new TierState(
                ResourceGroupTier.Interactive,
                rgOpts.Interactive.MaxConcurrency,
                rgOpts.Interactive.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.Interactive.TimeoutSeconds),
                tenantPercent,
                tenantAbsolute),
            [ResourceGroupTier.AutonomousAgents] = new TierState(
                ResourceGroupTier.AutonomousAgents,
                rgOpts.AutonomousAgents.MaxConcurrency,
                rgOpts.AutonomousAgents.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.AutonomousAgents.TimeoutSeconds),
                tenantPercent,
                tenantAbsolute),
            [ResourceGroupTier.BulkAnalytics] = new TierState(
                ResourceGroupTier.BulkAnalytics,
                rgOpts.BulkAnalytics.MaxConcurrency,
                rgOpts.BulkAnalytics.MaxQueueDepth,
                TimeSpan.FromSeconds(rgOpts.BulkAnalytics.TimeoutSeconds),
                tenantPercent,
                tenantAbsolute)
        };
    }

    public async ValueTask<ResourceGroupLeaseResult> TryAcquireLeaseAsync(
        ResourceGroupTier tier,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return ResourceGroupLeaseResult.Acquired(tier, EmptyLease.Instance);
        }

        if (!_tiers.TryGetValue(tier, out var state))
        {
            state = _tiers[ResourceGroupTier.Interactive];
        }

        return await state.TryAcquireAsync(_logger, tenantId, cancellationToken).ConfigureAwait(false);
    }

    public ResourceGroupMetrics GetMetrics()
    {
        var list = new List<ResourceGroupTierMetrics>(_tiers.Count);
        foreach (var kvp in _tiers)
        {
            list.Add(kvp.Value.GetMetrics());
        }

        return new ResourceGroupMetrics(DateTimeOffset.UtcNow, list);
    }

    /// <summary>
    /// SEC H-07: Number of leases currently held by the tenant in the given tier.
    /// </summary>
    public int GetTenantActiveCount(ResourceGroupTier tier, string tenantId) =>
        _tiers.TryGetValue(tier, out var state) ? state.GetTenantActiveCount(tenantId) : 0;

    /// <summary>
    /// SEC H-07: Number of tenant entries currently tracked for the tier (unused entries are removed).
    /// </summary>
    public int GetTrackedTenantCount(ResourceGroupTier tier) =>
        _tiers.TryGetValue(tier, out var state) ? state.TrackedTenantCount : 0;

    /// <summary>
    /// SEC H-07: Effective per-tenant concurrency limit of the tier.
    /// </summary>
    public int GetMaxConcurrencyPerTenant(ResourceGroupTier tier) =>
        _tiers.TryGetValue(tier, out var state) ? state.MaxConcurrencyPerTenant : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _tiers.Values)
        {
            state.Dispose();
        }
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public static readonly EmptyLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TierState : IDisposable
    {
        private const string AnonymousTenantKey = "__no-tenant__";

        public readonly ResourceGroupTier Tier;
        public readonly int MaxConcurrency;
        public readonly int MaxQueueDepth;
        public readonly TimeSpan Timeout;
        public readonly int MaxConcurrencyPerTenant;
        public readonly int MaxQueueDepthPerTenant;
        private readonly SemaphoreSlim _semaphore;

        // SEC H-07: per-tenant share of the tier. Entries are reference counted and removed when unused,
        // so the dictionary is bounded by the number of in-flight requests.
        private readonly object _tenantSync = new();
        private readonly Dictionary<string, TenantSlot> _tenants = new(StringComparer.OrdinalIgnoreCase);

        private int _queuedRequests;
        private int _tenantWaiting;
        private long _totalAcquired;
        private long _totalRejectedQueueFull;
        private long _totalRejectedTimeout;
        private bool _disposed;

        public TierState(
            ResourceGroupTier tier,
            int maxConcurrency,
            int maxQueueDepth,
            TimeSpan timeout,
            int tenantPercent,
            int tenantAbsolute)
        {
            Tier = tier;
            MaxConcurrency = Math.Max(1, maxConcurrency);
            MaxQueueDepth = Math.Max(0, maxQueueDepth);
            Timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5);
            _semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            MaxConcurrencyPerTenant = tenantAbsolute > 0
                ? Math.Min(tenantAbsolute, MaxConcurrency)
                : Math.Max(1, (int)((long)MaxConcurrency * Math.Clamp(tenantPercent, 1, 100) / 100));
            MaxQueueDepthPerTenant = MaxQueueDepth == 0
                ? 0
                : Math.Max(1, (int)((long)MaxQueueDepth * MaxConcurrencyPerTenant / MaxConcurrency));
        }

        public int TrackedTenantCount
        {
            get
            {
                lock (_tenantSync)
                {
                    return _tenants.Count;
                }
            }
        }

        public int GetTenantActiveCount(string tenantId)
        {
            lock (_tenantSync)
            {
                return _tenants.TryGetValue(NormalizeTenant(tenantId), out var slot) ? Volatile.Read(ref slot.Active) : 0;
            }
        }

        public async ValueTask<ResourceGroupLeaseResult> TryAcquireAsync(
            ILogger logger,
            string tenantId,
            CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            var slot = RentTenantSlot(NormalizeTenant(tenantId));
            var tenantHeld = false;
            var handedOver = false;

            try
            {
                // Stage 1 (SEC H-07): tenant share. Fast path only if no other request of this tenant is waiting.
                if (Volatile.Read(ref slot.Queued) == 0 && slot.Semaphore.Wait(0, CancellationToken.None))
                {
                    tenantHeld = true;
                }
                else
                {
                    int tenantQueue = Interlocked.Increment(ref slot.Queued);
                    int tenantWaiting = Interlocked.Increment(ref _tenantWaiting);
                    try
                    {
                        if (tenantQueue > MaxQueueDepthPerTenant || tenantWaiting > MaxQueueDepth)
                        {
                            Interlocked.Increment(ref _totalRejectedQueueFull);
                            logger.LogWarning(
                                "Resource group {Tier} rejected request from tenant {TenantId}: tenant share exhausted ({MaxPerTenant} concurrent, {MaxTenantQueue} queued)",
                                Tier, tenantId, MaxConcurrencyPerTenant, MaxQueueDepthPerTenant);
                            return ResourceGroupLeaseResult.QueueFull(Tier);
                        }

                        if (!await slot.Semaphore.WaitAsync(Timeout, ct).ConfigureAwait(false))
                        {
                            return RejectTimeout(logger, tenantId);
                        }

                        tenantHeld = true;
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref _totalRejectedTimeout);
                        throw;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _tenantWaiting);
                        Interlocked.Decrement(ref slot.Queued);
                    }
                }

                // Stage 2: global tier slot. Fast path only if no requests are queued (avoid barging).
                if (Volatile.Read(ref _queuedRequests) == 0 && _semaphore.Wait(0, CancellationToken.None))
                {
                    handedOver = true;
                    return Acquired(slot);
                }

                int currentQueue = Interlocked.Increment(ref _queuedRequests);
                if (currentQueue > MaxQueueDepth)
                {
                    Interlocked.Decrement(ref _queuedRequests);
                    Interlocked.Increment(ref _totalRejectedQueueFull);
                    logger.LogWarning(
                        "Resource group {Tier} rejected request from tenant {TenantId}: queue full ({CurrentQueue}/{MaxQueue})",
                        Tier, tenantId, currentQueue - 1, MaxQueueDepth);
                    return ResourceGroupLeaseResult.QueueFull(Tier);
                }

                try
                {
                    var remaining = Timeout - Stopwatch.GetElapsedTime(started);
                    bool entered = remaining > TimeSpan.Zero
                        ? await _semaphore.WaitAsync(remaining, ct).ConfigureAwait(false)
                        : _semaphore.Wait(0, CancellationToken.None);
                    if (!entered)
                    {
                        return RejectTimeout(logger, tenantId);
                    }

                    handedOver = true;
                    return Acquired(slot);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _totalRejectedTimeout);
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _queuedRequests);
                }
            }
            finally
            {
                if (!handedOver)
                {
                    if (tenantHeld)
                    {
                        SafeRelease(slot.Semaphore);
                    }

                    ReturnTenantSlot(slot);
                }
            }
        }

        public ResourceGroupTierMetrics GetMetrics()
        {
            int active = Math.Clamp(MaxConcurrency - _semaphore.CurrentCount, 0, MaxConcurrency);
            return new ResourceGroupTierMetrics(
                Tier,
                ActiveConcurrency: active,
                MaxConcurrency: MaxConcurrency,
                QueuedRequests: Math.Max(0, Volatile.Read(ref _queuedRequests)) + Math.Max(0, Volatile.Read(ref _tenantWaiting)),
                MaxQueueDepth: MaxQueueDepth,
                TotalAcquired: Volatile.Read(ref _totalAcquired),
                TotalRejectedQueueFull: Volatile.Read(ref _totalRejectedQueueFull),
                TotalRejectedTimeout: Volatile.Read(ref _totalRejectedTimeout)
            );
        }

        public void Release(TenantSlot slot)
        {
            SafeRelease(_semaphore);
            Interlocked.Decrement(ref slot.Active);
            SafeRelease(slot.Semaphore);
            ReturnTenantSlot(slot);
        }

        public void Dispose()
        {
            _semaphore.Dispose();
            lock (_tenantSync)
            {
                _disposed = true;
                foreach (var slot in _tenants.Values)
                {
                    slot.Semaphore.Dispose();
                }

                _tenants.Clear();
            }
        }

        private ResourceGroupLeaseResult Acquired(TenantSlot slot)
        {
            Interlocked.Increment(ref slot.Active);
            Interlocked.Increment(ref _totalAcquired);
            return ResourceGroupLeaseResult.Acquired(Tier, new LeaseScope(this, slot));
        }

        private ResourceGroupLeaseResult RejectTimeout(ILogger logger, string tenantId)
        {
            Interlocked.Increment(ref _totalRejectedTimeout);
            logger.LogWarning(
                "Resource group {Tier} request from tenant {TenantId} timed out after {TimeoutMs}ms",
                Tier, tenantId, Timeout.TotalMilliseconds);
            return ResourceGroupLeaseResult.TimedOut(Tier);
        }

        private TenantSlot RentTenantSlot(string key)
        {
            lock (_tenantSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_tenants.TryGetValue(key, out var slot))
                {
                    slot = new TenantSlot(key, MaxConcurrencyPerTenant);
                    _tenants[key] = slot;
                }

                slot.RefCount++;
                return slot;
            }
        }

        private void ReturnTenantSlot(TenantSlot slot)
        {
            lock (_tenantSync)
            {
                slot.RefCount--;
                if (slot.RefCount <= 0
                    && _tenants.TryGetValue(slot.Key, out var current)
                    && ReferenceEquals(current, slot))
                {
                    _tenants.Remove(slot.Key);
                    slot.Semaphore.Dispose();
                }
            }
        }

        private static void SafeRelease(SemaphoreSlim semaphore)
        {
            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string NormalizeTenant(string? tenantId) =>
            string.IsNullOrWhiteSpace(tenantId) ? AnonymousTenantKey : tenantId;
    }

    private sealed class TenantSlot : IDisposable
    {
        public readonly string Key;
        public readonly SemaphoreSlim Semaphore;
        public int Active;
        public int Queued;
        public int RefCount;

        public TenantSlot(string key, int maxConcurrency)
        {
            Key = key;
            Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        public void Dispose()
        {
            Semaphore.Dispose();
        }
    }

    private sealed class LeaseScope : IAsyncDisposable
    {
        private TierState? _owner;
        private readonly TenantSlot _slot;

        public LeaseScope(TierState owner, TenantSlot slot)
        {
            _owner = owner;
            _slot = slot;
        }

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(_slot);
            return ValueTask.CompletedTask;
        }
    }
}
