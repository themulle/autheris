namespace Autheris.Infrastructure.Rebac;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>
/// RR-L4-04 / INF-15: Cluster-wide, persistent ReBAC tuple store backed by Redis/Garnet.
/// Employs length-prefixed field keys and secondary index sets for fast (obj, relation) and (user) lookups
/// avoiding expensive HashGetAll per graph step.
/// Fail-closed: Redis errors are propagated to the caller and never fall back to a local, divergent copy.
/// </summary>
public sealed class RedisRebacStore : IRebacStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisRebacStore> _logger;
    private readonly string _prefix;

    public RedisRebacStore(IConnectionMultiplexer multiplexer, IOptions<GatewayOptions> options, ILogger<RedisRebacStore> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "autheris:";
        _prefix = prefix.EndsWith(':') ? prefix : prefix + ":";
    }

    public async ValueTask AddTupleAsync(RebacTuple tuple, CancellationToken ct = default)
    {
        ValidateTuple(tuple);
        var parsedUser = RebacSubject.Parse(tuple.User);
        if (!parsedUser.IsValid || !string.Equals(parsedUser.Canonical, tuple.User.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"ReBAC tuple 'User' '{tuple.User}' is not in canonical form.", nameof(tuple));
        }

        var db = _multiplexer.GetDatabase();
        var normalized = tuple with { TenantId = NormalizeTenant(tuple.TenantId) };
        var tenant = normalized.TenantId.ToLowerInvariant();
        var field = BuildField(normalized);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        var tran = db.CreateTransaction();
        _ = tran.HashSetAsync(TenantHashKey(tenant), field, json);
        _ = tran.SetAddAsync(ObjectIndexKey(tenant, normalized.Object, normalized.Relation), field);
        _ = tran.SetAddAsync(UserIndexKey(tenant, normalized.User), field);
        bool committed = await tran.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw new InvalidOperationException("Failed to commit ReBAC add transaction to Redis.");
        }
        _logger.LogDebug("RR-L4-04 ReBAC added tuple {Tuple} (redis)", normalized);
    }

    public async ValueTask AddTuplesAsync(IEnumerable<RebacTuple> tuples, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        foreach (var t in tuples)
        {
            ct.ThrowIfCancellationRequested();
            await AddTupleAsync(t, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> DeleteTupleAsync(RebacTuple tuple, CancellationToken ct = default)
    {
        ValidateTuple(tuple);
        var db = _multiplexer.GetDatabase();
        var tenant = NormalizeTenant(tuple.TenantId).ToLowerInvariant();
        var normalized = tuple with { TenantId = tenant };
        var field = BuildField(normalized);

        var tran = db.CreateTransaction();
        var delTask = tran.HashDeleteAsync(TenantHashKey(tenant), field);
        _ = tran.SetRemoveAsync(ObjectIndexKey(tenant, normalized.Object, normalized.Relation), field);
        _ = tran.SetRemoveAsync(UserIndexKey(tenant, normalized.User), field);
        bool committed = await tran.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw new InvalidOperationException("Failed to commit ReBAC delete transaction to Redis.");
        }
        bool removed = await delTask.ConfigureAwait(false);
        if (removed)
        {
            _logger.LogDebug("RR-L4-04 ReBAC removed tuple {Tuple} (redis)", tuple);
        }
        return removed;
    }

    public async ValueTask<IReadOnlyList<RebacTuple>> GetTuplesAsync(
        string tenantId,
        string? user = null,
        string? relation = null,
        string? obj = null,
        CancellationToken ct = default)
    {
        var tenant = NormalizeTenant(tenantId).ToLowerInvariant();
        var db = _multiplexer.GetDatabase();
        var now = DateTimeOffset.UtcNow;

        // 1. All three given -> HGET on the exact field
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(relation) && !string.IsNullOrWhiteSpace(obj))
        {
            var field = BuildFieldKey(user, relation, obj);
            var val = await db.HashGetAsync(TenantHashKey(tenant), field).ConfigureAwait(false);
            if (val.IsNullOrEmpty)
            {
                return Array.Empty<RebacTuple>();
            }

            var t = JsonSerializer.Deserialize<RebacTuple>(val.ToString(), JsonOptions);
            if (t is null || t.IsExpired(now))
            {
                return Array.Empty<RebacTuple>();
            }

            if (!string.Equals(NormalizeTenant(t.TenantId), tenant, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<RebacTuple>();
            }

            return [t];
        }

        // 2. obj + relation given -> SMEMBERS on the object index, then HMGET
        if (!string.IsNullOrWhiteSpace(obj) && !string.IsNullOrWhiteSpace(relation))
        {
            var members = await db.SetMembersAsync(ObjectIndexKey(tenant, obj, relation)).ConfigureAwait(false);
            if (members.Length == 0)
            {
                return Array.Empty<RebacTuple>();
            }

            var values = await db.HashGetAsync(TenantHashKey(tenant), members).ConfigureAwait(false);
            return FilterTuples(values, tenant, user, relation, obj, now);
        }

        // 3. user only (or user with relation) -> SMEMBERS on the user index, then HMGET
        if (!string.IsNullOrWhiteSpace(user) && string.IsNullOrWhiteSpace(obj))
        {
            var members = await db.SetMembersAsync(UserIndexKey(tenant, user)).ConfigureAwait(false);
            if (members.Length == 0)
            {
                return Array.Empty<RebacTuple>();
            }

            var values = await db.HashGetAsync(TenantHashKey(tenant), members).ConfigureAwait(false);
            return FilterTuples(values, tenant, user, relation, obj, now);
        }

        // 4. no filter (admin only) or partial without index -> HGETALL
        var entries = await db.HashGetAllAsync(TenantHashKey(tenant)).ConfigureAwait(false);
        var result = new List<RebacTuple>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Value.IsNullOrEmpty) continue;
            var t = JsonSerializer.Deserialize<RebacTuple>(entry.Value.ToString(), JsonOptions);
            if (t is null || t.IsExpired(now)) continue;

            if (!string.Equals(NormalizeTenant(t.TenantId), tenant, StringComparison.OrdinalIgnoreCase)) continue;
            if (user != null && (string.IsNullOrWhiteSpace(user) || !string.Equals(t.User, user, StringComparison.OrdinalIgnoreCase))) continue;
            if (relation != null && (string.IsNullOrWhiteSpace(relation) || !string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase))) continue;
            if (obj != null && (string.IsNullOrWhiteSpace(obj) || !string.Equals(t.Object, obj, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(t);
        }

        return result;
    }

    private static IReadOnlyList<RebacTuple> FilterTuples(
        RedisValue[] values,
        string tenant,
        string? user,
        string? relation,
        string? obj,
        DateTimeOffset now)
    {
        var result = new List<RebacTuple>(values.Length);
        foreach (var val in values)
        {
            if (val.IsNullOrEmpty) continue; // INF-15: fail-closed: dangling index member skipped
            var t = JsonSerializer.Deserialize<RebacTuple>(val.ToString(), JsonOptions);
            if (t is null || t.IsExpired(now)) continue;

            if (!string.Equals(NormalizeTenant(t.TenantId), tenant, StringComparison.OrdinalIgnoreCase)) continue;
            if (user != null && !string.Equals(t.User, user, StringComparison.OrdinalIgnoreCase)) continue;
            if (relation != null && !string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase)) continue;
            if (obj != null && !string.Equals(t.Object, obj, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(t);
        }
        return result;
    }

    public async ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = NormalizeTenant(tenantId).ToLowerInvariant();
        var db = _multiplexer.GetDatabase();

        var pattern = (RedisValue)$"{_prefix}rebac:{tenant}:*";
        var keysToDelete = new List<RedisKey>();

        try
        {
            foreach (var endpoint in _multiplexer.GetEndPoints())
            {
                var server = _multiplexer.GetServer(endpoint);
                if (server.IsConnected)
                {
                    keysToDelete.AddRange(server.Keys(pattern: pattern));
                }
            }
        }
        catch
        {
            // If server scan fails, proceed with direct hash key delete
        }

        if (keysToDelete.Count > 0)
        {
            await db.KeyDeleteAsync(keysToDelete.Distinct().ToArray()).ConfigureAwait(false);
        }
        else
        {
            await db.KeyDeleteAsync(TenantHashKey(tenant)).ConfigureAwait(false);
        }

        _logger.LogInformation("RR-L4-04 ReBAC cleared all tuples for tenant '{Tenant}' (redis)", tenant);
    }

    private static void ValidateTuple(RebacTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        if (string.IsNullOrWhiteSpace(tuple.User))
            throw new ArgumentException("ReBAC tuple 'User' must not be null or whitespace.", nameof(tuple));
        if (string.IsNullOrWhiteSpace(tuple.Relation))
            throw new ArgumentException("ReBAC tuple 'Relation' must not be null or whitespace.", nameof(tuple));
        if (string.IsNullOrWhiteSpace(tuple.Object))
            throw new ArgumentException("ReBAC tuple 'Object' must not be null or whitespace.", nameof(tuple));
        if (string.IsNullOrWhiteSpace(tuple.TenantId))
            throw new ArgumentException("ReBAC tuple 'TenantId' must not be null or whitespace.", nameof(tuple));
        if (tuple.Object.Contains(':'))
        {
            var parts = tuple.Object.Split(':', 2);
            if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                throw new ArgumentException("ReBAC tuple 'Object' contains an empty namespace or entity id.", nameof(tuple));
        }
    }

    private RedisKey TenantHashKey(string tenant) => $"{_prefix}rebac:{tenant}:tuples";

    private RedisKey ObjectIndexKey(string tenant, string obj, string relation)
    {
        var o = obj.ToLowerInvariant();
        var r = relation.ToLowerInvariant();
        return $"{_prefix}rebac:{tenant}:obj:{o.Length}:{o}#{r.Length}:{r}";
    }

    private RedisKey UserIndexKey(string tenant, string user)
    {
        var u = user.ToLowerInvariant();
        return $"{_prefix}rebac:{tenant}:user:{u.Length}:{u}";
    }

    private static string NormalizeTenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim();

    // INF-15: Ambiguity-free length-prefixed lower-case field key
    public static string BuildFieldKey(string user, string relation, string obj)
    {
        var u = user.ToLowerInvariant();
        var r = relation.ToLowerInvariant();
        var o = obj.ToLowerInvariant();
        return $"{u.Length}:{u}|{r.Length}:{r}|{o.Length}:{o}";
    }

    private static RedisValue BuildField(RebacTuple t) =>
        BuildFieldKey(t.User, t.Relation, t.Object);
}
