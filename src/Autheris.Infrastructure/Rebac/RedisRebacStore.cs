using System.Text.Json;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Rebac;

/// <summary>
/// RR-L4-04: Cluster-wide, persistent ReBAC tuple store backed by Redis/Garnet.
/// Every replica reads and writes the same per-tenant hash, so grants and revocations take effect
/// across all nodes (the evaluator decision caches are invalidated via the event bus, see
/// <see cref="Autheris.Application.Security.Rebac.Services.ZanzibarRebacEvaluator"/>).
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
        var db = _multiplexer.GetDatabase();
        var normalized = tuple with { TenantId = NormalizeTenant(tuple.TenantId) };
        await db.HashSetAsync(TenantKey(normalized.TenantId), BuildField(normalized), JsonSerializer.Serialize(normalized, JsonOptions)).ConfigureAwait(false);
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
        var tenant = NormalizeTenant(tuple.TenantId);
        var removed = await db.HashDeleteAsync(TenantKey(tenant), BuildField(tuple with { TenantId = tenant })).ConfigureAwait(false);
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
        var tenant = NormalizeTenant(tenantId);
        var db = _multiplexer.GetDatabase();
        var entries = await db.HashGetAllAsync(TenantKey(tenant)).ConfigureAwait(false);

        var result = new List<RebacTuple>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Value.IsNullOrEmpty) continue;
            var t = JsonSerializer.Deserialize<RebacTuple>(entry.Value.ToString(), JsonOptions);
            if (t is null) continue;

            // Defense in depth: never return a tuple whose embedded tenant differs from the partition.
            if (!string.Equals(NormalizeTenant(t.TenantId), tenant, StringComparison.OrdinalIgnoreCase)) continue;
            if (user != null)
            {
                if (string.IsNullOrWhiteSpace(user) || !string.Equals(t.User, user, StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (relation != null)
            {
                if (string.IsNullOrWhiteSpace(relation) || !string.Equals(t.Relation, relation, StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (obj != null)
            {
                if (string.IsNullOrWhiteSpace(obj) || !string.Equals(t.Object, obj, StringComparison.OrdinalIgnoreCase)) continue;
            }
            result.Add(t);
        }

        return result;
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

    public async ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = NormalizeTenant(tenantId);
        await _multiplexer.GetDatabase().KeyDeleteAsync(TenantKey(tenant)).ConfigureAwait(false);
        _logger.LogInformation("RR-L4-04 ReBAC cleared all tuples for tenant '{Tenant}' (redis)", tenant);
    }

    private RedisKey TenantKey(string tenant) => $"{_prefix}rebac:tuples:{tenant.ToLowerInvariant()}";

    private static string NormalizeTenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim();

    // Same identity semantics as InMemoryRebacStore (case-insensitive user#relation@object).
    private static RedisValue BuildField(RebacTuple t) =>
        $"{t.User}#{t.Relation}@{t.Object}".ToLowerInvariant();
}
