using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

public sealed class ConsentCacheService : IConsentCacheService, IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly IEpochValidationService _epochValidationService;
    private readonly IBinaryCacheSerializer? _serializer;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IDatabase? _redisDb;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tableCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _subscription;

    private readonly object _syncLock = new();
    private readonly byte[] _l2IntegrityKey;
    private readonly ILogger<ConsentCacheService>? _logger;

    // SEC H-01: L2 entries are framed as [version(1)][expiresAtUnixMs(8, BE)][HMAC-SHA256(32)][payload].
    private const byte L2FormatVersion = 0x01;
    private const int L2HeaderLength = 1 + 8 + 32;
    private static readonly byte[] L2MacDomain = "Autheris:ConsentCacheL2:v1"u8.ToArray();
    private static readonly TimeSpan MaxL1PromotionTtl = TimeSpan.FromMinutes(5);

    private sealed record CacheEntryEnvelope(TableAccessDecision Decision, long Epoch);

    public ConsentCacheService(
        IMemoryCache memoryCache,
        IEpochValidationService epochValidationService,
        IEventBus eventBus,
        IOptions<GatewayOptions>? options = null,
        IBinaryCacheSerializer? serializer = null,
        IConnectionMultiplexer? multiplexer = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ILogger<ConsentCacheService>? logger = null)
    {
        _memoryCache = memoryCache;
        _epochValidationService = epochValidationService;
        _serializer = serializer;
        _multiplexer = multiplexer;
        _redisDb = multiplexer?.GetDatabase();
        _logger = logger;
        _l2IntegrityKey = ResolveL2IntegrityKey(options?.Value, secretProvider, logger, multiplexer != null && serializer != null);
        _prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "Autheris:";
        if (!_prefix.EndsWith(':'))
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
    }

    private static readonly Prometheus.Counter CacheHits = Prometheus.Metrics.CreateCounter(
        "autheris_consent_cache_hits_total", "Number of ConsentCache hits");
    private static readonly Prometheus.Counter CacheMisses = Prometheus.Metrics.CreateCounter(
        "autheris_consent_cache_misses_total", "Number of ConsentCache misses");

    public Task<TableAccessDecision?> GetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
        => GetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, contextHash, ct);

    public async Task<TableAccessDecision?> GetCachedDecisionAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string? contextHash = null,
        CancellationToken ct = default)
    {
        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        if (_memoryCache.TryGetValue(cacheKey, out CacheEntryEnvelope? envelope) && envelope != null)
        {
            // Validate epoch
            var isValid = await _epochValidationService.IsEpochValidAsync(table, envelope.Epoch, ct).ConfigureAwait(false);
            if (!isValid)
            {
                CacheMisses.Inc();
                _memoryCache.Remove(cacheKey);
                RemoveKeyFromTableIndex(table, cacheKey);
                return null;
            }

            CacheHits.Inc();
            return envelope.Decision;
        }

        // L1 Miss: Check L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var rawValue = await _redisDb.StringGetAsync(l2Key).ConfigureAwait(false);
                byte[]? payloadBytes = null;
                DateTimeOffset l2ExpiresAt = default;
                byte[]? rawBytes = rawValue.IsNullOrEmpty ? null : (byte[]?)rawValue;
                if (rawBytes != null && rawBytes.Length > 0)
                {
                    payloadBytes = UnprotectL2Payload(l2Key.ToString(), rawBytes, out l2ExpiresAt);
                    if (payloadBytes == null)
                    {
                        // SEC H-01: forged, replayed-after-expiry or corrupted L2 entry -> treat as miss and drop it.
                        _logger?.LogWarning("Consent L2 cache entry failed integrity verification and was discarded (table {Table}).", table.ToString());
                        await _redisDb.KeyDeleteAsync(l2Key).ConfigureAwait(false);
                    }
                }

                if (payloadBytes != null && _serializer.TryDeserialize<CachedConsentEnvelope>(payloadBytes, out var l2Env) && l2Env != null && MatchesTable(l2Env, table))
                {
                    var isL2EpochValid = await _epochValidationService.IsEpochValidAsync(table, l2Env.Epoch, ct).ConfigureAwait(false);
                    if (isL2EpochValid)
                    {
                        var decision = l2Env.ToDecision();
                        // RR-L4-06: never extend the original (security-relevant) TTL on promotion.
                        var remaining = l2ExpiresAt - DateTimeOffset.UtcNow;
                        var l1Ttl = remaining < MaxL1PromotionTtl ? remaining : MaxL1PromotionTtl;
                        if (l1Ttl > TimeSpan.Zero)
                        {
                            SetL1Internal(table, cacheKey, new CacheEntryEnvelope(decision, l2Env.Epoch), l1Ttl);
                        }

                        CacheHits.Inc();
                        return decision;
                    }
                    else
                    {
                        await _redisDb.KeyDeleteAsync(l2Key).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Fallback gracefully on L2 error
            }
        }

        CacheMisses.Inc();
        RemoveKeyFromTableIndex(table, cacheKey);
        return null;
    }

    public Task SetCachedDecisionAsync(
        Sid userSid,
        TableIdentifier table,
        TableAccessDecision decision,
        TimeSpan ttl,
        string? contextHash = null,
        CancellationToken ct = default)
        => SetCachedDecisionAsync(TenantId.LegacySingleTenant, userSid, table, decision, ttl, contextHash, ct);

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

        var cacheKey = BuildCacheKey(tenant, userSid, table, contextHash);
        var currentEpoch = await _epochValidationService.GetCurrentEpochAsync(table, ct).ConfigureAwait(false);

        // RR-L4-06: compare-and-set. If the epoch moved while consents were being loaded (revocation / policy change),
        // the decision may be stale and must not be cached at all.
        if (epochAtLoad.HasValue && epochAtLoad.Value != currentEpoch)
        {
            _logger?.LogDebug("Consent decision for table {Table} not cached: epoch changed during evaluation.", table.ToString());
            return;
        }

        var storedEpoch = epochAtLoad ?? currentEpoch;
        var envelope = new CacheEntryEnvelope(decision, storedEpoch);

        SetL1Internal(table, cacheKey, envelope, ttl);

        // Store in L2 Distributed Cache (Redis / Garnet via MemoryPack)
        if (_redisDb != null && _serializer != null)
        {
            try
            {
                var l2Key = (RedisKey)$"{_prefix}consent:l2:{cacheKey}";
                var l2Dto = CachedConsentEnvelope.FromDecision(decision, storedEpoch);
                var binaryPayload = _serializer.Serialize(l2Dto);
                var protectedPayload = ProtectL2Payload(l2Key.ToString(), binaryPayload, DateTimeOffset.UtcNow.Add(ttl));
                await _redisDb.StringSetAsync(l2Key, protectedPayload, ttl).ConfigureAwait(false);
            }
            catch
            {
                // L1 remains active even if L2 fails
            }
        }
    }

    private void SetL1Internal(TableIdentifier table, string cacheKey, CacheEntryEnvelope envelope, TimeSpan ttl)
    {
        var decision = envelope.Decision;

        // Approximate byte size for L1 MemoryCache size budgeting (NF-PERF-02)
        var estimatedBytes = 512
            + (decision.CombinedRowFilterSql?.Length ?? 0) * 2
            + decision.ColumnAccess.Count * 64
            + decision.DeniedReasons.Sum(r => r.Length * 2);

        var tableKey = NormalizeTableKey(table);

        var cacheEntryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = Math.Max(256, estimatedBytes)
        };

        cacheEntryOptions.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            lock (_syncLock)
            {
                if (_tableCacheKeys.TryGetValue(tableKey, out var currentKeys))
                {
                    currentKeys.TryRemove((string)evictedKey, out _);
                    if (currentKeys.IsEmpty)
                    {
                        _tableCacheKeys.TryRemove(tableKey, out _);
                    }
                }
            }
        });

        var keys = _tableCacheKeys.GetOrAdd(tableKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        keys.TryAdd(cacheKey, 0);
        _memoryCache.Set(cacheKey, envelope, cacheEntryOptions);
    }

    public Task EvictTableDecisionsAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var tableKey = NormalizeTableKey(table);
        if (_tableCacheKeys.TryRemove(tableKey, out var keys))
        {
            foreach (var key in keys.Keys)
            {
                _memoryCache.Remove(key);
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearL1CacheAsync(CancellationToken ct = default)
    {
        lock (_syncLock)
        {
            if (_memoryCache is MemoryCache mc)
            {
                mc.Clear();
            }
            _tableCacheKeys.Clear();
        }
        return Task.CompletedTask;
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

    public static string ComputeSubjectContextHash(IReadOnlySet<Sid>? groupSids, IReadOnlySet<string>? roles) =>
        IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);

    private static string BuildCacheKey(TenantId tenant, Sid userSid, TableIdentifier table, string? contextHash = null)
    {
        var safeSid = Uri.EscapeDataString(userSid.Value.ToUpperInvariant());
        var safeContext = Uri.EscapeDataString(string.IsNullOrWhiteSpace(contextHash) ? "default" : contextHash);
        return $"{tenant.Value}:consent:{safeSid}:{safeContext}:{table.Domain.ToLowerInvariant()}:{table.Schema.ToLowerInvariant()}:{table.TableName.ToLowerInvariant()}";
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
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > expiresAtMs)
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
    }
}
