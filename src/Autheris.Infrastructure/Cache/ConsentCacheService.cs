using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

public sealed class ConsentCacheService : IConsentCacheService, IGovernanceCacheInvalidator, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IEpochValidationService _epochValidationService;
    private readonly IBinaryCacheSerializer? _serializer;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IDatabase? _redisDb;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _tableCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tenantCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TableIdentifier>> _tenantTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _tenantGenerations = new(StringComparer.OrdinalIgnoreCase);
    private static long _keySequence;
    private long _globalGeneration;
    private readonly TimeProvider _timeProvider;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _catalogVersionProvider;
    private readonly IDisposable? _subscription;
    private readonly IDisposable? _governanceSubscription;

    private readonly object _syncLock = new();
    private readonly byte[] _l2IntegrityKey;
    private readonly ILogger<ConsentCacheService>? _logger;
    private readonly string _restrictionFingerprint;

    // SEC H-01: L2 entries are framed as [version(1)][expiresAtUnixMs(8, BE)][HMAC-SHA256(32)][payload].
    private const byte L2FormatVersion = 0x01;
    private const int L2HeaderLength = 1 + 8 + 32;
    private static readonly byte[] L2MacDomain = "Autheris:ConsentCacheL2:v1"u8.ToArray();
    private static readonly TimeSpan MaxL1PromotionTtl = TimeSpan.FromMinutes(5);
    private static readonly RedisValue GlobalGenField = (RedisValue)"*";
    private RedisKey ClusterGenKey => (RedisKey)$"{_prefix}consent:gen";

    /// <summary>R-3: the faults of the distributed tier (connection, timeout, frame crypto, serialization) that may degrade to a miss.</summary>
    private static bool IsL2Failure(Exception ex) =>
        ex is RedisException or TimeoutException or CryptographicException or MemoryPack.MemoryPackSerializationException;

    private sealed record CacheEntryEnvelope(
        TableAccessDecision Decision,
        long Epoch,
        DateTimeOffset ValidUntil,
        long TenantGeneration = 0,
        long GlobalGeneration = 0);

    public ConsentCacheService(
        IMemoryCache memoryCache,
        IEpochValidationService epochValidationService,
        IEventBus eventBus,
        IOptions<GatewayOptions>? options = null,
        IBinaryCacheSerializer? serializer = null,
        IConnectionMultiplexer? multiplexer = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger<ConsentCacheService>? logger = null,
        Autheris.Domain.Security.IRestrictedScopePolicy? restrictedScopes = null,
        TimeProvider? timeProvider = null,
        Autheris.Application.Catalog.ICatalogVersionProvider? catalogVersionProvider = null,
        IGovernanceChangeNotifier? changeNotifier = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalogVersionProvider = catalogVersionProvider;
        _memoryCache = memoryCache;
        _epochValidationService = epochValidationService;
        _serializer = serializer;
        _multiplexer = multiplexer;
        _redisDb = multiplexer?.GetDatabase();
        _logger = logger;
        _restrictionFingerprint = (restrictedScopes ?? Autheris.Domain.Security.RestrictedScopePolicy.Default).Fingerprint;
        _l2IntegrityKey = ResolveL2IntegrityKey(options?.Value, secretProvider, logger, multiplexer != null && serializer != null);
        _prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "Autheris:";
        if (!_prefix.EndsWith(':', StringComparison.Ordinal))
        {
            _prefix += ":";
        }

        var channel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _subscription = eventBus.Subscribe<string>(channel, async tableString =>
        {
            await Task.Yield();
            var normalized = (tableString ?? string.Empty).ToLowerInvariant();
            lock (_syncLock)
            {
                if (_tableCacheKeys.TryRemove(normalized, out var keys))
                {
                    foreach (var key in keys.Keys)
                    {
                        _memoryCache.Remove(key);
                    }
                }
            }
        });

        _governanceSubscription = changeNotifier?.Subscribe(change =>
        {
            if (change.IsFullInvalidation)
            {
                InvalidateAll();
            }
            else
            {
                InvalidateTenant(change.TenantId);
            }
        });
    }

    private static readonly Meter Meter = new("Autheris.Core");
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("autheris.cache.requests", description: "Number of cache requests");

    private static void RecordCacheHit() =>
        CacheRequests.Add(1, new KeyValuePair<string, object?>("autheris.cache.name", "consent"), new KeyValuePair<string, object?>("autheris.cache.result", "hit"));

    private static void RecordCacheMiss() =>
        CacheRequests.Add(1, new KeyValuePair<string, object?>("autheris.cache.name", "consent"), new KeyValuePair<string, object?>("autheris.cache.result", "miss"));

    public async Task<IReadOnlyDictionary<TableIdentifier, TableAccessDecision?>> GetCachedDecisionsAsync(
        TenantId tenant,
        Sid userSid,
        IReadOnlyList<TableIdentifier> tables,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, TableAccessDecision?>();
        if (tables == null || tables.Count == 0)
        {
            return result;
        }

        contextHash = await ResolveEffectiveContextHashAsync(contextHash, ct).ConfigureAwait(false);
        var (localGlobalBefore, localTenantBefore) = CaptureLocalGenerations(tenant.Value);
        var now = _timeProvider.GetUtcNow();
        var candidateEntries = new Dictionary<TableIdentifier, (string CacheKey, long Epoch, TableAccessDecision Decision, DateTimeOffset ValidUntil, bool FromL2, TimeSpan? L1PromotionTtl, RedisKey? L2Key)>();
        var missingTables = new List<TableIdentifier>();

        foreach (var table in tables)
        {
            var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
            if (_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) && envelope != null)
            {
                if (envelope.ValidUntil == default || now >= envelope.ValidUntil || !IsGenerationCurrent(tenant.Value, envelope))
                {
                    _memoryCache.Remove(cacheKey);
                    RemoveKeyFromTableIndex(table, cacheKey);
                    missingTables.Add(table);
                }
                else
                {
                    candidateEntries[table] = (cacheKey, envelope.Epoch, envelope.Decision, envelope.ValidUntil, false, null, null);
                }
            }
            else
            {
                missingTables.Add(table);
            }
        }

        // L1 Misses: check L2
        if (missingTables.Count > 0 && _redisDb != null && _serializer != null)
        {
            try
            {
                // issue every L2 read first (the multiplexer pipelines them), then await them together.
                // Single-key commands only, so this is also safe on Redis Cluster.
                var l2Keys = new RedisKey[missingTables.Count];
                var l2Reads = new Task<RedisValue>[missingTables.Count];
                for (var i = 0; i < l2Reads.Length; i++)
                {
                    l2Keys[i] = (RedisKey)$"{_prefix}consent:l2:{BuildCacheKey(tenant, userSid, missingTables[i], contextHash)}";
                    l2Reads[i] = _redisDb.StringGetAsync(l2Keys[i]);
                }

                await Task.WhenAll(l2Reads).ConfigureAwait(false);

                var decodedL2 = new List<(TableIdentifier Table, string CacheKey, RedisKey L2Key, CachedConsentEnvelope Env, TableAccessDecision Decision, DateTimeOffset ExpiresAt)>();
                for (var i = 0; i < missingTables.Count; i++)
                {
                    var table = missingTables[i];
                    var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
                    var decoded = await TryDecodeL2EntryAsync(table, l2Keys[i], l2Reads[i].Result, now).ConfigureAwait(false);
                    if (decoded is { } entry)
                    {
                        decodedL2.Add((table, cacheKey, l2Keys[i], entry.Env, entry.Decision, entry.ExpiresAt));
                    }
                    else
                    {
                        result[table] = null;
                        RecordCacheMiss();
                        RemoveKeyFromTableIndex(table, cacheKey);
                    }
                }

                if (decodedL2.Count > 0)
                {
                    var (clusterGlobal, clusterTenant) = await GetClusterGenerationsAsync(tenant.Value, ct).ConfigureAwait(false);
                    foreach (var item in decodedL2)
                    {
                        if (item.Env.ClusterGlobalGeneration != clusterGlobal || item.Env.ClusterTenantGeneration != clusterTenant)
                        {
                            await _redisDb.KeyDeleteAsync(item.L2Key).ConfigureAwait(false);
                            result[item.Table] = null;
                            RecordCacheMiss();
                            RemoveKeyFromTableIndex(item.Table, item.CacheKey);
                            continue;
                        }

                        var l1Ttl = ComputeL1PromotionTtl(item.ExpiresAt, item.Env.ValidUntil, now);
                        candidateEntries[item.Table] = (item.CacheKey, item.Env.Epoch, item.Decision, item.Env.ValidUntil, true, l1Ttl > TimeSpan.Zero ? l1Ttl : null, item.L2Key);
                    }
                }
            }
            catch (Exception ex) when (IsL2Failure(ex))
            {
                // A genuine L2 fault degrades to a cache miss (the decision is re-evaluated); anything else is a bug and propagates.
                _logger?.LogWarning(ConsentCacheLogEvents.L2ReadFailed, ex, "Consent L2 cache read failed; treating as a cache miss.");
                foreach (var table in missingTables)
                {
                    if (!result.ContainsKey(table) && !candidateEntries.ContainsKey(table))
                    {
                        result[table] = null;
                        RecordCacheMiss();
                        RemoveKeyFromTableIndex(table, BuildCacheKey(tenant, userSid, table, contextHash));
                    }
                }
            }
        }
        else
        {
            foreach (var table in missingTables)
            {
                var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
                result[table] = null;
                RecordCacheMiss();
                RemoveKeyFromTableIndex(table, cacheKey);
            }
        }

        if (candidateEntries.Count == 0)
        {
            return result;
        }

        // Validate candidate epochs in batch
        var checks = candidateEntries.Select(kv => new EpochCheck(kv.Key, kv.Value.Epoch, false)).ToList();
        var validationResults = await _epochValidationService.AreEpochsValidAsync(checks, ct).ConfigureAwait(false);

        foreach (var (table, candidate) in candidateEntries)
        {
            bool isValid = validationResults.TryGetValue(table, out var v) && v
                && IsLocalGenerationUnchanged(tenant.Value, localGlobalBefore, localTenantBefore);
            if (isValid)
            {
                if (candidate.FromL2 && candidate.L1PromotionTtl.HasValue)
                {
                    SetL1Internal(table, candidate.CacheKey, new CacheEntryEnvelope(candidate.Decision, candidate.Epoch, candidate.ValidUntil), candidate.L1PromotionTtl.Value);
                }

                RecordCacheHit();
                result[table] = candidate.Decision;
            }
            else
            {
                RecordCacheMiss();
                if (!candidate.FromL2)
                {
                    _memoryCache.Remove(candidate.CacheKey);
                }
                else if (candidate.L2Key.HasValue && _redisDb != null && !(validationResults.TryGetValue(table, out var epochOk) && epochOk))
                {
                    try
                    {
                        await _redisDb.KeyDeleteAsync(candidate.L2Key.Value).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsL2Failure(ex))
                    {
                        _logger?.LogWarning(ConsentCacheLogEvents.L2DeleteFailed, ex, "Consent L2 cache delete of a stale entry failed; the entry expires by TTL.");
                    }
                }
                RemoveKeyFromTableIndex(table, candidate.CacheKey);
                result[table] = null;
            }
        }

        return result;
    }

    public async Task<TableAccessDecision?> GetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        contextHash = await ResolveEffectiveContextHashAsync(contextHash, ct).ConfigureAwait(false);
        var (localGlobalBefore, localTenantBefore) = CaptureLocalGenerations(tenant.Value);
        var now = _timeProvider.GetUtcNow();
        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        if (_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) && envelope != null)
        {
            if (envelope.ValidUntil == default || now >= envelope.ValidUntil || !IsGenerationCurrent(tenant.Value, envelope))
            {
                RecordCacheMiss();
                _memoryCache.Remove(cacheKey);
                RemoveKeyFromTableIndex(table, cacheKey);
                return null;
            }

            // Validate epoch
            var isValid = await _epochValidationService.IsEpochValidAsync(table, envelope.Epoch, ct).ConfigureAwait(false);
            if (!isValid || !IsGenerationCurrent(tenant.Value, envelope))
            {
                RecordCacheMiss();
                _memoryCache.Remove(cacheKey);
                RemoveKeyFromTableIndex(table, cacheKey);
                return null;
            }

            RecordCacheHit();
            return envelope.Decision;
        }

        // L1 Miss: Check L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var rawValue = await _redisDb.StringGetAsync(l2Key).ConfigureAwait(false);
                if (await TryDecodeL2EntryAsync(table, l2Key, rawValue, now).ConfigureAwait(false) is { } entry)
                {
                    var (clusterGlobal, clusterTenant) = await GetClusterGenerationsAsync(tenant.Value, ct).ConfigureAwait(false);
                    var isClusterCurrent = entry.Env.ClusterGlobalGeneration == clusterGlobal && entry.Env.ClusterTenantGeneration == clusterTenant;
                    var isL2EpochValid = isClusterCurrent && await _epochValidationService.IsEpochValidAsync(table, entry.Env.Epoch, ct).ConfigureAwait(false);
                    if (!isClusterCurrent || !isL2EpochValid)
                    {
                        await _redisDb.KeyDeleteAsync(l2Key).ConfigureAwait(false);
                    }
                    else if (IsLocalGenerationUnchanged(tenant.Value, localGlobalBefore, localTenantBefore))
                    {
                        var l1Ttl = ComputeL1PromotionTtl(entry.ExpiresAt, entry.Env.ValidUntil, now);
                        if (l1Ttl > TimeSpan.Zero)
                            SetL1Internal(table, cacheKey, new CacheEntryEnvelope(entry.Decision, entry.Env.Epoch, entry.Env.ValidUntil), l1Ttl);
                        RecordCacheHit();
                        return entry.Decision;
                    }
                }
            }
            catch (Exception ex) when (IsL2Failure(ex))
            {
                // A genuine L2 fault degrades to a cache miss (the decision is re-evaluated); anything else is a bug and propagates.
                _logger?.LogWarning(ConsentCacheLogEvents.L2ReadFailed, ex, "Consent L2 cache read failed; treating as a cache miss.");
            }
        }

        RecordCacheMiss();
        RemoveKeyFromTableIndex(table, cacheKey);
        return null;
    }

    public Task SetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash = null,
        CancellationToken ct = default)
        => SetCachedDecisionAsync(tenant, userSid, table, decision, ttl, contextHash, epochAtLoad: null, ct);

    public async Task<long?> GetEpochSnapshotAsync(TableIdentifier table, CancellationToken ct = default)
        => await _epochValidationService.GetCurrentEpochAsync(table, ct).ConfigureAwait(false);

    public async Task SetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash,
        long? epochAtLoad,
        CancellationToken ct = default)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        contextHash = await ResolveEffectiveContextHashAsync(contextHash, ct).ConfigureAwait(false);
        var (localGlobalBefore, localTenantBefore) = CaptureLocalGenerations(tenant.Value);
        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        var currentEpoch = await _epochValidationService.GetCurrentEpochAsync(table, ct).ConfigureAwait(false);

        // RR-L4-06: compare-and-set. If the epoch moved while consents were being loaded (revocation / policy change),
        // the decision may be stale and must not be cached at all.
        if ((epochAtLoad.HasValue && epochAtLoad.Value != currentEpoch) ||
            !IsLocalGenerationUnchanged(tenant.Value, localGlobalBefore, localTenantBefore))
        {
            _logger?.LogDebug("Consent decision for table {Table} not cached: epoch changed during evaluation.", table.ToString());
            return;
        }

        var storedEpoch = epochAtLoad ?? currentEpoch;
        var now = _timeProvider.GetUtcNow();
        var validUntil = now.Add(ttl);
        var envelope = new CacheEntryEnvelope(decision, storedEpoch, validUntil);

        SetL1Internal(table, cacheKey, envelope, ttl);

        // Store in L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var (clusterGlobal, clusterTenant) = await GetClusterGenerationsAsync(tenant.Value, ct).ConfigureAwait(false);
                if (!IsLocalGenerationUnchanged(tenant.Value, localGlobalBefore, localTenantBefore))
                {
                    return;
                }

                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var l2Dto = CachedConsentEnvelope.FromDecision(decision, storedEpoch, _restrictionFingerprint, validUntil, clusterGlobal, clusterTenant);
                var binaryPayload = _serializer.Serialize(l2Dto);
                var protectedPayload = ProtectL2Payload(l2Key.ToString(), binaryPayload, validUntil);
                await _redisDb.StringSetAsync(l2Key, protectedPayload, ttl).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsL2Failure(ex))
            {
                // L1 remains active even if L2 fails.
                _logger?.LogWarning(ConsentCacheLogEvents.L2WriteFailed, ex, "Consent L2 cache write failed; the entry stays in L1 only.");
            }
        }
    }

    private void SetL1Internal(TableIdentifier table, string cacheKey, CacheEntryEnvelope envelope, TimeSpan ttl)
    {
        var decision = envelope.Decision;
        var tenantKey = ExtractTenantKey(cacheKey);
        var stampedEnvelope = envelope with
        {
            TenantGeneration = _tenantGenerations.GetOrAdd(tenantKey, 0),
            GlobalGeneration = Volatile.Read(ref _globalGeneration)
        };

        // Approximate byte size for L1 MemoryCache size budgeting (NF-PERF-02)
        var estimatedBytes = 512
            + (decision.CombinedRowFilterSql?.Length ?? 0) * 2
            + decision.ColumnAccess.Count * 64
            + decision.DeniedReasons.Sum(r => r.Length * 2);

        var tableKey = NormalizeTableKey(table);

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            SlidingExpiration = TimeSpan.FromMinutes(15),
            Size = Math.Max(256, estimatedBytes),
            Priority = CacheItemPriority.Low
        };

        var entrySeq = Interlocked.Increment(ref _keySequence);

        cacheEntryOptions.RegisterPostEvictionCallback((evictedKey, _, reason, _) =>
        {
            if (reason == EvictionReason.Replaced)
            {
                return;
            }

            lock (_syncLock)
            {
                if (_tableCacheKeys.TryGetValue(tableKey, out var currentKeys))
                {
                    currentKeys.TryRemove(new KeyValuePair<string, long>((string)evictedKey, entrySeq));
                    if (currentKeys.IsEmpty)
                    {
                        _tableCacheKeys.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, long>>(tableKey, currentKeys));
                    }
                }

                if (!string.IsNullOrEmpty(tenantKey) && _tenantCacheKeys.TryGetValue(tenantKey, out var tenantKeys))
                {
                    tenantKeys.TryRemove((string)evictedKey, out _);
                }
            }
        });

        lock (_syncLock)
        {
            const int maxTrackedKeysPerTable = 5000;
            var keys = _tableCacheKeys.GetOrAdd(tableKey, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
            if (keys.Count >= maxTrackedKeysPerTable)
            {
                // Evict oldest 20% tracked keys from L1 to prevent memory exhaustion (FIFO/LRU)
                var keysToRemove = keys.OrderBy(kv => kv.Value).Take(maxTrackedKeysPerTable / 5).Select(kv => kv.Key).ToList();
                foreach (var k in keysToRemove)
                {
                    keys.TryRemove(k, out _);
                    _memoryCache.Remove(k);
                }
            }

            keys[cacheKey] = entrySeq;

            if (!string.IsNullOrEmpty(tenantKey))
            {
                var tenantKeys = _tenantCacheKeys.GetOrAdd(tenantKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                tenantKeys[cacheKey] = 0;
                var tenantTables = _tenantTables.GetOrAdd(tenantKey, _ => new ConcurrentDictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase));
                tenantTables[tableKey] = table;
            }
        }

        _memoryCache.Set(cacheKey, stampedEnvelope, cacheEntryOptions);
    }

    private bool IsGenerationCurrent(string tenantId, CacheEntryEnvelope envelope) =>
        envelope.GlobalGeneration == Volatile.Read(ref _globalGeneration)
        && envelope.TenantGeneration == _tenantGenerations.GetOrAdd(tenantId, 0);

    private static string ExtractTenantKey(string cacheKey)
    {
        var colonIndex = cacheKey.IndexOf(':', StringComparison.Ordinal);
        return colonIndex > 0 ? cacheKey[..colonIndex] : string.Empty;
    }

    private async ValueTask<(CachedConsentEnvelope Env, TableAccessDecision Decision, DateTimeOffset ExpiresAt)?> TryDecodeL2EntryAsync(
        TableIdentifier table, RedisKey l2Key, RedisValue rawValue, DateTimeOffset now)
    {
        byte[]? rawBytes = rawValue.IsNullOrEmpty ? null : (byte[]?)rawValue;
        byte[]? payloadBytes = null;
        DateTimeOffset expiresAt = default;
        if (rawBytes is { Length: > 0 })
        {
            payloadBytes = UnprotectL2Payload(l2Key.ToString(), rawBytes, out expiresAt);
            if (payloadBytes == null)
            {
                _logger?.LogWarning("Consent L2 cache entry failed integrity verification and was discarded (table {Table}).", table.ToString());
                await _redisDb!.KeyDeleteAsync(l2Key).ConfigureAwait(false);
                return null;
            }
        }

        if (payloadBytes == null || !_serializer!.TryDeserialize<CachedConsentEnvelope>(payloadBytes, out var l2Env) || l2Env == null || !MatchesTable(l2Env, table))
            return null;

        if (l2Env.ToDecision(_restrictionFingerprint, now) is not { } decision)
        {
            await _redisDb!.KeyDeleteAsync(l2Key).ConfigureAwait(false);
            return null;
        }

        return (l2Env, decision, expiresAt);
    }

    private static TimeSpan ComputeL1PromotionTtl(DateTimeOffset l2ExpiresAt, DateTimeOffset envValidUntil, DateTimeOffset now)
    {
        var remaining = (l2ExpiresAt < envValidUntil ? l2ExpiresAt : envValidUntil) - now;
        return remaining < MaxL1PromotionTtl ? remaining : MaxL1PromotionTtl;
    }

    private (long Global, long Tenant) CaptureLocalGenerations(string tenantId) =>
        (Volatile.Read(ref _globalGeneration), _tenantGenerations.GetOrAdd(tenantId, 0));

    private bool IsLocalGenerationUnchanged(string tenantId, long expectedGlobal, long expectedTenant) =>
        Volatile.Read(ref _globalGeneration) == expectedGlobal && _tenantGenerations.GetOrAdd(tenantId, 0) == expectedTenant;

    private async ValueTask<(long Global, long Tenant)> GetClusterGenerationsAsync(string tenantId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_redisDb == null) return (0L, 0L);
        var values = await _redisDb.HashGetAsync(ClusterGenKey, [GlobalGenField, (RedisValue)tenantId]).ConfigureAwait(false);
        return values is { Length: >= 2 } ? (ParseGeneration(values[0]), ParseGeneration(values[1])) : (0L, 0L);
    }

    private static long ParseGeneration(RedisValue value) =>
        value.IsNullOrEmpty ? 0L : (long.TryParse(value.ToString(), out var parsed) ? parsed : 0L);

    private Task? DispatchClusterGenerationBump(bool global, string? tenantId)
    {
        if (_redisDb == null) return null;
        var field = global || string.IsNullOrWhiteSpace(tenantId) ? GlobalGenField : (RedisValue)tenantId.Trim();
        try { return _redisDb.HashIncrementAsync(ClusterGenKey, field, 1); }
        catch (Exception ex) when (IsL2Failure(ex)) { _logger?.LogWarning(ConsentCacheLogEvents.L2WriteFailed, ex, "Consent L2 cluster generation increment failed."); return null; }
    }

    private async Task ObserveClusterBumpAsync(Task? bumpTask)
    {
        if (bumpTask == null) return;
        try { await bumpTask.ConfigureAwait(false); }
        catch (Exception ex) when (IsL2Failure(ex)) { _logger?.LogWarning(ConsentCacheLogEvents.L2WriteFailed, ex, "Consent L2 cluster generation increment failed."); }
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            InvalidateAll();
            return;
        }

        var normalizedTenant = tenantId.Trim();
        _tenantGenerations.AddOrUpdate(normalizedTenant, 1, static (_, gen) => gen + 1);
        var clusterBump = DispatchClusterGenerationBump(global: false, normalizedTenant);

        IReadOnlyList<TableIdentifier> tables = [];
        lock (_syncLock)
        {
            if (_tenantCacheKeys.TryRemove(normalizedTenant, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    _memoryCache.Remove(key);
                }
            }

            if (_tenantTables.TryRemove(normalizedTenant, out var trackedTables))
            {
                tables = trackedTables.Values.ToArray();
            }
        }

        _ = ObserveClusterBumpAsync(clusterBump);
        foreach (var table in tables)
        {
            _ = InvalidateEpochSafeAsync(table);
        }
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _globalGeneration);
        _tenantGenerations.Clear();
        var clusterBump = DispatchClusterGenerationBump(global: true, tenantId: null);

        IReadOnlyList<TableIdentifier> allTables;
        lock (_syncLock)
        {
            if (_memoryCache is MemoryCache mc)
            {
                mc.Clear();
            }
            else
            {
                foreach (var keys in _tenantCacheKeys.Values)
                {
                    foreach (var key in keys.Keys)
                    {
                        _memoryCache.Remove(key);
                    }
                }
            }

            _tableCacheKeys.Clear();
            _tenantCacheKeys.Clear();
            allTables = _tenantTables.Values.SelectMany(d => d.Values).Distinct().ToArray();
            _tenantTables.Clear();
        }

        _ = ObserveClusterBumpAsync(clusterBump);
        foreach (var table in allTables)
        {
            _ = InvalidateEpochSafeAsync(table);
        }
    }

    private async Task InvalidateEpochSafeAsync(TableIdentifier table)
    {
        try
        {
            await _epochValidationService.InvalidateEpochAsync(table).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Best-effort epoch invalidation failed for table {Table}.", table.ToString());
        }
    }

    public async Task ClearL1CacheAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _globalGeneration);
        _tenantGenerations.Clear();
        var clusterBump = DispatchClusterGenerationBump(global: true, tenantId: null);
        lock (_syncLock)
        {
            if (_memoryCache is MemoryCache mc)
            {
                mc.Clear();
            }
            _tableCacheKeys.Clear();
            _tenantCacheKeys.Clear();
            _tenantTables.Clear();
        }
        await ObserveClusterBumpAsync(clusterBump).ConfigureAwait(false);
    }

    public int GetTrackedKeyCount(TableIdentifier table)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryGetValue(tableKey, out var keys))
        {
            return keys.Count;
        }
        return 0;
    }

    private void RemoveKeyFromTableIndex(TableIdentifier table, string cacheKey)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryGetValue(tableKey, out var keys))
        {
            keys.TryRemove(cacheKey, out _);
            if (keys.IsEmpty)
            {
                _tableCacheKeys.TryRemove(tableKey, out _);
            }
        }
    }

    private static string NormalizeTableKey(TableIdentifier table) => table.ToString().ToLowerInvariant();

    public static string ComputeSubjectContextHash(IReadOnlySet<Sid>? groupSids, IReadOnlySet<string>? roles, IReadOnlySet<string>? scopes = null) =>
        IConsentCacheService.ComputeSubjectContextHash(groupSids, roles, scopes);

    // INF-03: ':' is legal in table components, so each part is escaped to keep the key unambiguous.
    private static string EscapeTablePart(string part) => Uri.EscapeDataString(part.ToLowerInvariant());

    private async ValueTask<string?> ResolveEffectiveContextHashAsync(string? contextHash, CancellationToken ct)
    {
        if (_catalogVersionProvider is null)
        {
            return contextHash;
        }

        var version = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        var baseContext = string.IsNullOrWhiteSpace(contextHash) ? "default" : contextHash;
        return $"{baseContext}|cat:{version.StructureVersion}.{version.ContentVersion}";
    }

    private static string BuildCacheKey(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null)
    {
        StoredTenant.RequireWritable(tenant); // DT-SC-04: default(TenantId) never keys a cache entry
        var safeSid = Uri.EscapeDataString(userSid.Value.ToUpperInvariant());
        var safeContext = Uri.EscapeDataString(string.IsNullOrWhiteSpace(contextHash) ? "default" : contextHash);
        return $"{tenant.Value}:consent:{safeSid}:{safeContext}:{EscapeTablePart(table.Domain)}:{EscapeTablePart(table.Schema)}:{EscapeTablePart(table.TableName)}";
    }

    private static bool MatchesTable(CachedConsentEnvelope env, TableIdentifier table)
    {
        var envDomain = string.IsNullOrWhiteSpace(env.Domain) ? "default" : env.Domain;
        var tableDomain = string.IsNullOrWhiteSpace(table.Domain) ? "default" : table.Domain;
        return string.Equals(envDomain, tableDomain, StringComparison.OrdinalIgnoreCase)
            && string.Equals(env.Schema ?? string.Empty, table.Schema ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(env.TableName ?? string.Empty, table.TableName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal byte[] ProtectL2Payload(string l2Key, byte[] payload, DateTimeOffset expiresAt)
    {
        var framed = new byte[L2HeaderLength + payload.Length];
        framed[0] = L2FormatVersion;
        BinaryPrimitives.WriteInt64BigEndian(framed.AsSpan(1, 8), expiresAt.ToUnixTimeMilliseconds());
        payload.CopyTo(framed.AsSpan(L2HeaderLength));
        var mac = ComputeL2Mac(l2Key, framed.AsSpan(1, 8), payload);
        mac.CopyTo(framed.AsSpan(9, 32));
        return framed;
    }

    internal byte[]? UnprotectL2Payload(string l2Key, byte[] framed)
        => UnprotectL2Payload(l2Key, framed, out _);

    internal byte[]? UnprotectL2Payload(string l2Key, byte[] framed, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (framed.Length <= L2HeaderLength || framed[0] != L2FormatVersion)
        {
            return null;
        }

        var expiresBytes = framed.AsSpan(1, 8);
        var payload = framed.AsSpan(L2HeaderLength).ToArray();
        var expected = ComputeL2Mac(l2Key, expiresBytes, payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, framed.AsSpan(9, 32)))
        {
            return null;
        }

        var expiresAtMs = BinaryPrimitives.ReadInt64BigEndian(expiresBytes);
        if (_timeProvider.GetUtcNow().ToUnixTimeMilliseconds() > expiresAtMs)
        {
            return null;
        }

        expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs);
        return payload;
    }

    private byte[] ComputeL2Mac(string l2Key, ReadOnlySpan<byte> expiresBytes, byte[] payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(l2Key);
        Span<byte> lengthPrefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, keyBytes.Length);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _l2IntegrityKey);
        hmac.AppendData(L2MacDomain);
        hmac.AppendData(lengthPrefix);
        hmac.AppendData(keyBytes);
        hmac.AppendData(expiresBytes);
        hmac.AppendData(payload);
        return hmac.GetHashAndReset();
    }

    private static byte[] ResolveL2IntegrityKey(
        GatewayOptions? options,
        IKeyVaultSecretProvider? secretProvider,
        ILogger? logger,
        bool l2Enabled)
    {
        var secretRef = options?.Caching?.Redis?.L2IntegrityKeyVaultRef;
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            secretRef = options?.DataMasking?.HmacSecretKeyVaultRef;
        }

        if (secretProvider != null && !string.IsNullOrWhiteSpace(secretRef))
        {
            try
            {
                var masterKey = secretProvider.GetSecretBytes(secretRef);
                if (masterKey != null && masterKey.Length > 0)
                {
                    // SEC H-01: dedicated HKDF sub-key, cryptographically separated from masking and audit keys.
                    return HKDF.DeriveKey(
                        HashAlgorithmName.SHA256,
                        masterKey,
                        32,
                        info: "Autheris:ConsentCacheL2:v1"u8.ToArray());
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Consent L2 integrity key could not be resolved; falling back to an ephemeral per-process key (L2 entries are not shared across nodes).");
            }
        }
        else if (l2Enabled)
        {
            logger?.LogWarning("No secret provider/key reference for the consent L2 integrity key; using an ephemeral per-process key (L2 entries are not shared across nodes).");
        }

        // Fail-safe: an unknown random key keeps L2 entries unforgeable; cross-node sharing simply misses.
        return RandomNumberGenerator.GetBytes(32);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _governanceSubscription?.Dispose();
    }
}
