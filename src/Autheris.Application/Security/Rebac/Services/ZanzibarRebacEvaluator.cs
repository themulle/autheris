namespace Autheris.Application.Security.Rebac.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SEC-04: Zanzibar/OpenFGA-compliant Graph Traversal ReBAC Evaluator.
/// Resolves direct, inherited, and hierarchical relationships with cycle detection,
/// recursion bounding, and batch-evaluation capabilities.
/// </summary>
public sealed class ZanzibarRebacEvaluator : IRebacEvaluator
{
    private readonly IRebacStore _store;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<ZanzibarRebacEvaluator> _logger;

    // Relation -> Relations that inherit it (e.g., "viewer" is inherited by ["editor", "owner"])
    private readonly ConcurrentDictionary<string, HashSet<string>> _inheritedBy = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CachedDecision(bool Allowed, DateTimeOffset Expiry, long Generation);

    // Decision cache: TenantId -> (CacheKey -> CachedDecision)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CachedDecision>> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Degraded tenant state when generation bump fails: bypasses decision cache until bump succeeds
    private readonly ConcurrentDictionary<string, bool> _degradedTenants = new(StringComparer.OrdinalIgnoreCase);

    // Local generation cache: TenantId -> (Generation, ExpiryTimestamp)
    private readonly ConcurrentDictionary<string, (long Generation, DateTimeOffset ExpiresAt)> _tenantGenerationCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RR-L4-04: Event-bus channel used to invalidate decision caches on every replica.</summary>
    public const string InvalidationChannel = "autheris:rebac:invalidate";

    /// <summary>Monotonic cluster generation key stored in the broker to detect partition drift on reconnect.</summary>
    public const string GenerationKey = "rebac:generation";

    public static string GetTenantGenerationKey(string tenant) => $"rebac:generation:{tenant}";

    private readonly IEventBus? _eventBus;

    public ZanzibarRebacEvaluator(
        IRebacStore store,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<ZanzibarRebacEvaluator> logger,
        IEventBus? eventBus = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        if (gatewayOptions.Value?.Rebac?.PerCheckMemoMaxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayOptions), "PerCheckMemoMaxEntries must be greater than 0.");
        }
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;

        InitializeDefaultInheritance();

        if (_eventBus != null)
        {
            _eventBus.ConnectionRestored += () =>
            {
                _logger.LogInformation("Event bus connection restored. Evicting ReBAC decision cache to synchronize with cluster (1.8).");
                _cache.Clear();
                _tenantGenerationCache.Clear();
                _degradedTenants.Clear();
            };

            // Singleton for the application lifetime; the subscription lives as long as the event bus.
            _ = _eventBus.Subscribe<string>(InvalidationChannel, tenant =>
            {
                InvalidateLocal(tenant);
                return Task.CompletedTask;
            });
        }
    }

    public bool IsEnabled => _gatewayOptions.Value.Rebac.Enabled;

    public void RegisterInheritance(string baseRelation, params string[] inheritedBy)
    {
        ArgumentNullException.ThrowIfNull(baseRelation);
        var set = _inheritedBy.GetOrAdd(baseRelation, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        lock (set)
        {
            foreach (var rel in inheritedBy)
            {
                set.Add(rel);
            }
        }
    }

    public async Task InvalidateTenantCacheAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = InvalidateLocal(tenantId);

        if (_eventBus != null)
        {
            try
            {
                var tenantKey = GetTenantGenerationKey(tenant);
                await _eventBus.IncrementCounterAsync(tenantKey, ct).ConfigureAwait(false);
                await _eventBus.IncrementCounterAsync(GenerationKey, ct).ConfigureAwait(false);
                await _eventBus.PublishAsync(InvalidationChannel, tenant, ct).ConfigureAwait(false);

                // Successfully bumped: clear degraded state for tenant if any
                _degradedTenants.TryRemove(tenant, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReBAC cluster generation bump or publish failed for tenant {Tenant}. Entering degraded state.", tenant);
                _degradedTenants[tenant] = true;
                InvalidateLocal(tenant);
                throw;
            }
        }
    }

    private string InvalidateLocal(string? tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim();
        _tenantGenerationCache.TryRemove(tenant, out _);
        if (_cache.TryRemove(tenant, out _))
        {
            _logger.LogDebug("F-SEC-04 ReBAC invalidated decision cache for tenant {Tenant}", tenant);
        }
        return tenant;
    }

    private async ValueTask<long?> GetTenantGenerationAsync(string tenant, CancellationToken ct)
    {
        if (_eventBus == null)
        {
            return 0L;
        }

        var now = DateTimeOffset.UtcNow;
        var cacheMs = _gatewayOptions.Value.Rebac?.GenerationCacheMilliseconds ?? 1000;
        if (cacheMs > 0 && _tenantGenerationCache.TryGetValue(tenant, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Generation;
        }

        try
        {
            var tenantGenKey = GetTenantGenerationKey(tenant);
            var gen = await _eventBus.GetCounterAsync(tenantGenKey, ct).ConfigureAwait(false);
            if (cacheMs > 0)
            {
                _tenantGenerationCache[tenant] = (gen, now.AddMilliseconds(cacheMs));
            }
            return gen;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ReBAC generation counter for tenant {Tenant}. Cache validation failed-closed to cache miss.", tenant);
            return null;
        }
    }

    public async ValueTask<RebacCheckResult> CheckAsync(RebacCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsEnabled)
        {
            // If ReBAC disabled, allow (default permissive when disabled)
            return RebacCheckResult.Permitted;
        }

        // POL-7: Empty tuple fields must never act as wildcard or allow
        if (string.IsNullOrWhiteSpace(request.User) ||
            string.IsNullOrWhiteSpace(request.Relation) ||
            string.IsNullOrWhiteSpace(request.Object))
        {
            return RebacCheckResult.Denied;
        }

        // EP-08: Canonicalize request.User; invalid subject forms fail-closed to Denied.
        var parsedUser = RebacSubject.Parse(request.User);
        if (!parsedUser.IsValid)
        {
            return RebacCheckResult.Denied;
        }
        var canonicalUser = parsedUser.Canonical;

        var tenant = string.IsNullOrWhiteSpace(request.TenantId) ? "default" : request.TenantId.Trim();
        var cacheKey = $"{canonicalUser}#{request.Relation}@{request.Object}";

        var isDegraded = _degradedTenants.ContainsKey(tenant);
        long? currentGen = null;

        // 1. Check cache (only if tenant is not degraded)
        if (!isDegraded)
        {
            currentGen = await GetTenantGenerationAsync(tenant, ct).ConfigureAwait(false);
            if (currentGen.HasValue &&
                _cache.TryGetValue(tenant, out var tenantCache) &&
                tenantCache.TryGetValue(cacheKey, out var entry) &&
                entry.Expiry > DateTimeOffset.UtcNow &&
                entry.Generation == currentGen.Value)
            {
                return entry.Allowed ? RebacCheckResult.Permitted : RebacCheckResult.Denied;
            }
        }

        // 2. Perform graph resolution with cycle & depth guard
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracker = new ExpiryTracker();
        var rebacOptions = _gatewayOptions.Value.Rebac;
        var maxDepth = rebacOptions.MaxTraversalDepth;
        var memoMaxEntries = rebacOptions.PerCheckMemoMaxEntries;
        var memo = new Dictionary<(string? User, string? Relation, string? Obj), IReadOnlyList<RebacTuple>>();

        try
        {
            var allowed = await TraverseAndEvaluateAsync(tenant, canonicalUser, request.Relation, request.Object, depth: 0, maxDepth, onStack, visitedFailed, tracker, memo, memoMaxEntries, ct).ConfigureAwait(false);

            // 3. Store in cache (only if not degraded and generation is known)
            if (!isDegraded && currentGen.HasValue)
            {
                var ttl = rebacOptions.CacheTtlSeconds;
                var expiry = DateTimeOffset.UtcNow.AddSeconds(ttl);
                if (allowed && tracker.Earliest is { } tupleExpiry && tupleExpiry < expiry)
                {
                    expiry = tupleExpiry; // WP-SC-16: a decision never outlives the time-bound tuple it rests on
                }

                StoreInCache(tenant, cacheKey, allowed, expiry, currentGen.Value);
            }

            return allowed ? RebacCheckResult.Permitted : RebacCheckResult.Denied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "F-SEC-04 ReBAC evaluation failed closed due to store exception for {User}#{Rel}@{Obj} in tenant {Tenant}", canonicalUser, request.Relation, request.Object, tenant);
            return RebacCheckResult.Denied;
        }
    }

    /// <summary>Earliest expiry of the time-bound tuples a positive decision rests on (null: only standing tuples).</summary>
    private sealed class ExpiryTracker
    {
        public DateTimeOffset? Earliest { get; private set; }

        public void Note(DateTimeOffset? expiresAt)
        {
            if (expiresAt is { } e && (Earliest is null || e < Earliest))
            {
                Earliest = e;
            }
        }
    }

    /// <summary>Review E-3: bounded decision cache (arbitrary user/object combinations must not grow memory unbounded).</summary>
    internal const int MaxCachedTenants = 1_000;
    internal const int MaxCachedDecisionsPerTenant = 10_000;

    private void StoreInCache(string tenant, string cacheKey, bool allowed, DateTimeOffset expiry, long generation)
    {
        if (!_cache.TryGetValue(tenant, out var tc))
        {
            if (_cache.Count >= MaxCachedTenants)
            {
                return; // do not cache rather than grow without bound
            }

            tc = _cache.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, CachedDecision>(StringComparer.OrdinalIgnoreCase));
        }

        if (tc.Count >= MaxCachedDecisionsPerTenant)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in tc)
            {
                if (kvp.Value.Expiry <= now)
                {
                    tc.TryRemove(kvp.Key, out _);
                }
            }

            if (tc.Count >= MaxCachedDecisionsPerTenant)
            {
                tc.Clear();
            }
        }

        tc[cacheKey] = new CachedDecision(allowed, expiry, generation);
    }

    public async ValueTask<RebacBatchCheckResult> BatchCheckAsync(RebacBatchCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new Dictionary<RebacCheckRequest, bool>(request.Checks.Count);
        var batchTenant = string.IsNullOrWhiteSpace(request.TenantId) ? "default" : request.TenantId.Trim();

        foreach (var check in request.Checks)
        {
            if (ct.IsCancellationRequested) break;

            // Review E-3: defense in depth, an item from another tenant is denied instead of evaluated.
            var itemTenant = string.IsNullOrWhiteSpace(check.TenantId) ? "default" : check.TenantId.Trim();
            if (!string.Equals(itemTenant, batchTenant, StringComparison.OrdinalIgnoreCase))
            {
                results[check] = false;
                continue;
            }

            var res = await CheckAsync(check, ct).ConfigureAwait(false);
            results[check] = res.Allowed;
        }

        return new RebacBatchCheckResult(results);
    }

    private async Task<bool> TraverseAndEvaluateAsync(
        string tenantId,
        string user,
        string relation,
        string obj,
        int depth,
        int maxDepth,
        HashSet<string> onStack,
        HashSet<string> visitedFailed,
        ExpiryTracker tracker,
        Dictionary<(string? User, string? Relation, string? Obj), IReadOnlyList<RebacTuple>> memo,
        int memoMaxEntries,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        // POL-7: Reject empty fields in evaluation
        if (string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(relation) ||
            string.IsNullOrWhiteSpace(obj))
        {
            return false;
        }

        // Cyclic recursion guard (SEC-REBAC-01)
        if (depth > maxDepth)
        {
            _logger.LogWarning(
                "F-SEC-04 ReBAC recursion depth {Depth} exceeded max {MaxDepth} for {User}#{Rel}@{Obj}. Decision: Denied.",
                depth, maxDepth, user, relation, obj);
            return false;
        }

        var visitKey = $"{user}#{relation}@{obj}";
        if (visitedFailed.Contains(visitKey))
        {
            return false;
        }

        if (!onStack.Add(visitKey))
        {
            // True cycle detected on current call stack! Fail-closed
            _logger.LogWarning("F-SEC-04 ReBAC cyclic relationship detected: {CycleKey}. Decision: Denied.", visitKey);
            return false;
        }

        try
        {
            // 1. Direct check: Is there an exact tuple (tenant, user, relation, obj)?
            // WP-SC-16: expired tuples are filtered here as well, so a store that does not filter cannot grant access (fail closed).
            var now = DateTimeOffset.UtcNow;
            var directTuples = await GetTuplesWithMemoAsync(memo, memoMaxEntries, tenantId, user, relation, obj, ct).ConfigureAwait(false);
            var direct = directTuples
                .Where(t => !t.IsExpired(now) && !string.IsNullOrWhiteSpace(t.User) && string.Equals(t.User, user, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (direct.Count > 0)
            {
                tracker.Note(direct.Any(t => t.ExpiresAt is null) ? null : direct.Max(t => t.ExpiresAt));
                return true;
            }

            // 2. Transitive inheritance: Are there relations that inherit 'relation'? (e.g. editor -> viewer)
            if (_inheritedBy.TryGetValue(relation, out var parentRelations))
            {
                List<string> parents;
                lock (parentRelations)
                {
                    parents = parentRelations.ToList();
                }

                foreach (var parentRel in parents)
                {
                    if (await TraverseAndEvaluateAsync(tenantId, user, parentRel, obj, depth + 1, maxDepth, onStack, visitedFailed, tracker, memo, memoMaxEntries, ct).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }

            // 3. User-set expansion: Does user belong to a group that has this relation?
            // E.g., user is "user:alice", check if there's a tuple (tenantId, group, relation, obj) and alice is member of group
            var objectTuples = await GetTuplesWithMemoAsync(memo, memoMaxEntries, tenantId, user: null, relation: relation, obj: obj, ct).ConfigureAwait(false);
            foreach (var t in objectTuples)
            {
                if (string.IsNullOrWhiteSpace(t.User) || t.IsExpired(now))
                {
                    // POL-7: Empty User in tuple must never be treated as wildcard or allow! Expired tuples are absent.
                    continue;
                }

                if (t.User.Contains('#'))
                {
                    // User-set: e.g. "group:engineering#member"
                    var parts = t.User.Split('#');
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                    {
                        var groupObj = parts[0];
                        var groupRel = parts[1];
                        // Check if current user has 'groupRel' on 'groupObj'
                        if (await TraverseAndEvaluateAsync(tenantId, user, groupRel, groupObj, depth + 1, maxDepth, onStack, visitedFailed, tracker, memo, memoMaxEntries, ct).ConfigureAwait(false))
                        {
                            tracker.Note(t.ExpiresAt);
                            return true;
                        }
                    }
                }
            }

            // 4. Hierarchical parent delegation: Does 'obj' have a parent that grants access?
            // E.g. tuple (tenantId, parentObj, "parent", obj) -> check if user has relation on parentObj
            var parentTuples = await GetTuplesWithMemoAsync(memo, memoMaxEntries, tenantId, user: null, relation: "parent", obj: obj, ct).ConfigureAwait(false);
            foreach (var pt in parentTuples)
            {
                var parentEntity = pt.User; // In parent relation, User column stores the parent entity identifier
                if (string.IsNullOrWhiteSpace(parentEntity) || pt.IsExpired(now))
                {
                    // POL-7: Empty User in parent relation must never be treated as wildcard or parent!
                    continue;
                }

                if (await TraverseAndEvaluateAsync(tenantId, user, relation, parentEntity, depth + 1, maxDepth, onStack, visitedFailed, tracker, memo, memoMaxEntries, ct).ConfigureAwait(false))
                {
                    tracker.Note(pt.ExpiresAt);
                    return true;
                }
            }

            visitedFailed.Add(visitKey);
            return false;
        }
        finally
        {
            onStack.Remove(visitKey);
        }
    }

    private async ValueTask<IReadOnlyList<RebacTuple>> GetTuplesWithMemoAsync(
        Dictionary<(string? User, string? Relation, string? Obj), IReadOnlyList<RebacTuple>> memo,
        int memoMaxEntries,
        string tenantId,
        string? user,
        string? relation,
        string? obj,
        CancellationToken ct)
    {
        var key = (user, relation, obj);
        if (memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var results = await _store.GetTuplesAsync(tenantId, user, relation, obj, ct).ConfigureAwait(false);
        if (memo.Count < memoMaxEntries)
        {
            memo[key] = results;
        }
        return results;
    }

    private void InitializeDefaultInheritance()
    {
        // Standard Enterprise Hierarchy:
        // "viewer" is inherited by "editor" and "owner"
        // "editor" is inherited by "owner"
        RegisterInheritance("viewer", "editor", "owner");
        RegisterInheritance("editor", "owner");

        var inheritCanQueryFromViewer = _gatewayOptions.Value.Rebac?.InheritCanQueryFromViewer ?? true;
        if (inheritCanQueryFromViewer)
        {
            // "can_query" is inherited by "viewer", "editor" and "owner" (POL-6 / OLAP / Arrow)
            RegisterInheritance("can_query", "viewer", "editor", "owner");
        }
        else
        {
            // SR15-23: When viewer inheritance is disabled, only editor and owner inherit can_query
            RegisterInheritance("can_query", "editor", "owner");
        }
    }
}
