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

    // Decision cache: TenantId -> (CacheKey -> (Allowed, Expiry))
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (bool Allowed, DateTimeOffset Expiry)>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RR-L4-04: Event-bus channel used to invalidate decision caches on every replica.</summary>
    public const string InvalidationChannel = "autheris:rebac:invalidate";

    private readonly IEventBus? _eventBus;

    public ZanzibarRebacEvaluator(
        IRebacStore store,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<ZanzibarRebacEvaluator> logger,
        IEventBus? eventBus = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventBus = eventBus;

        InitializeDefaultInheritance();

        if (_eventBus != null)
        {
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

    public void InvalidateTenantCache(string tenantId)
    {
        var tenant = InvalidateLocal(tenantId);

        // RR-L4-04: propagate to all other replicas (Redis pub/sub when configured). Best effort;
        // the per-entry TTL (Rebac.CacheTtlSeconds) bounds staleness if a message is lost.
        if (_eventBus != null)
        {
            _ = PublishInvalidationAsync(tenant);
        }
    }

    private async Task PublishInvalidationAsync(string tenant)
    {
        try
        {
            await _eventBus!.PublishAsync(InvalidationChannel, tenant).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RR-L4-04 ReBAC cluster-wide cache invalidation for tenant {Tenant} could not be published.", tenant);
        }
    }

    private string InvalidateLocal(string? tenantId)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim();
        if (_cache.TryRemove(tenant, out _))
        {
            _logger.LogDebug("F-SEC-04 ReBAC invalidated decision cache for tenant {Tenant}", tenant);
        }
        return tenant;
    }

    public async ValueTask<RebacCheckResult> CheckAsync(RebacCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsEnabled)
        {
            // If ReBAC disabled, allow (default permissive when disabled)
            return RebacCheckResult.Permitted;
        }

        var tenant = string.IsNullOrWhiteSpace(request.TenantId) ? "default" : request.TenantId.Trim();
        var cacheKey = $"{request.User}#{request.Relation}@{request.Object}";

        // 1. Check cache
        if (_cache.TryGetValue(tenant, out var tenantCache) &&
            tenantCache.TryGetValue(cacheKey, out var entry) &&
            entry.Expiry > DateTimeOffset.UtcNow)
        {
            return entry.Allowed ? RebacCheckResult.Permitted : RebacCheckResult.Denied;
        }

        // 2. Perform graph resolution with cycle & depth guard
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxDepth = _gatewayOptions.Value.Rebac.MaxTraversalDepth;

        var allowed = await TraverseAndEvaluateAsync(tenant, request.User, request.Relation, request.Object, depth: 0, maxDepth, visited, ct).ConfigureAwait(false);

        // 3. Store in cache
        var ttl = _gatewayOptions.Value.Rebac.CacheTtlSeconds;
        var expiry = DateTimeOffset.UtcNow.AddSeconds(ttl);
        var tc = _cache.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, (bool, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase));
        tc[cacheKey] = (allowed, expiry);

        return allowed ? RebacCheckResult.Permitted : RebacCheckResult.Denied;
    }

    public async ValueTask<RebacBatchCheckResult> BatchCheckAsync(RebacBatchCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new Dictionary<RebacCheckRequest, bool>(request.Checks.Count);

        foreach (var check in request.Checks)
        {
            if (ct.IsCancellationRequested) break;
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
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        // Cyclic recursion guard (SEC-REBAC-01)
        if (depth > maxDepth)
        {
            _logger.LogWarning(
                "F-SEC-04 ReBAC recursion depth {Depth} exceeded max {MaxDepth} for {User}#{Rel}@{Obj}. Decision: Denied.",
                depth, maxDepth, user, relation, obj);
            return false;
        }

        var visitKey = $"{user}#{relation}@{obj}";
        if (!visited.Add(visitKey))
        {
            // Cycle detected! Fail-closed
            _logger.LogWarning("F-SEC-04 ReBAC cyclic relationship detected: {CycleKey}. Decision: Denied.", visitKey);
            return false;
        }

        // 1. Direct check: Is there an exact tuple (tenant, user, relation, obj)?
        var directTuples = await _store.GetTuplesAsync(tenantId, user, relation, obj, ct).ConfigureAwait(false);
        if (directTuples.Count > 0)
        {
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
                if (await TraverseAndEvaluateAsync(tenantId, user, parentRel, obj, depth + 1, maxDepth, visited, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }

        // 3. User-set expansion: Does user belong to a group that has this relation?
        // E.g., user is "user:alice", check if there's a tuple (tenantId, group, relation, obj) and alice is member of group
        var objectTuples = await _store.GetTuplesAsync(tenantId, user: null, relation: relation, obj: obj, ct).ConfigureAwait(false);
        foreach (var t in objectTuples)
        {
            if (t.User.Contains('#'))
            {
                // User-set: e.g. "group:engineering#member"
                var parts = t.User.Split('#');
                if (parts.Length == 2)
                {
                    var groupObj = parts[0];
                    var groupRel = parts[1];
                    // Check if current user has 'groupRel' on 'groupObj'
                    if (await TraverseAndEvaluateAsync(tenantId, user, groupRel, groupObj, depth + 1, maxDepth, visited, ct).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
            }
        }

        // 4. Hierarchical parent delegation: Does 'obj' have a parent that grants access?
        // E.g. tuple (tenantId, parentObj, "parent", obj) -> check if user has relation on parentObj
        var parentTuples = await _store.GetTuplesAsync(tenantId, user: null, relation: "parent", obj: obj, ct).ConfigureAwait(false);
        foreach (var pt in parentTuples)
        {
            var parentEntity = pt.User; // In parent relation, User column stores the parent entity identifier
            if (await TraverseAndEvaluateAsync(tenantId, user, relation, parentEntity, depth + 1, maxDepth, visited, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private void InitializeDefaultInheritance()
    {
        // Standard Enterprise Hierarchy:
        // "viewer" is inherited by "editor" and "owner"
        // "editor" is inherited by "owner"
        RegisterInheritance("viewer", "editor", "owner");
        RegisterInheritance("editor", "owner");
    }
}
