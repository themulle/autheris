namespace Autheris.Infrastructure.Streaming;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// WP-ARC-A-13: Cluster-wide CDC event channel backed by Redis Pub/Sub (<c>autheris:cdc:events</c>)
/// with a local <see cref="InMemoryCdcEventChannel"/> for in-process fan-out and history.
/// Uses a per-node instance identifier in the pub/sub envelope so events published on this node
/// are delivered locally once and are not double-delivered when echoed back from Redis.
/// <para>
/// Envelope security: CDC events carry full, unmasked before/after row images of every tenant, so the pub/sub envelope is
/// sealed with AES-256-GCM. The channel key is <c>HKDF-SHA256(CDC KEK, info "autheris-cdc-pubsub-v1")</c> over the CDC stream
/// KEK keyring (<see cref="ICdcKeyEncryptionKeyProvider"/>, <c>Gateway:CdcStream:Encryption</c>) — a key separate from the stream
/// DEK wrapping. The AAD binds the envelope version, KEK id, sender instance id, send timestamp, nonce and channel name.
/// A received message is dispatched locally only after it authenticates, lies within <see cref="MaxMessageAge"/> (and not more
/// than <see cref="MaxFutureSkew"/> ahead) and its nonce has not been seen (bounded replay cache). Without a usable KEK the
/// channel refuses to start (outside Development the KEK provider never hands out an ephemeral key).
/// </para>
/// <para>
/// Row values travel through the closed-tag <see cref="CdcPayloadCodec"/>, so <c>decimal</c>, <c>DateTime</c>,
/// <c>DateTimeOffset</c>, <c>DateOnly</c>, <c>TimeOnly</c>, <c>Guid</c> and <c>byte[]</c> keep their CLR type across nodes;
/// values outside the tag set are sent as their invariant string form.
/// </para>
/// </summary>
public sealed class RedisCdcEventChannel : ICdcEventChannel, IAsyncDisposable, IDisposable
{
    public const string PubSubChannelName = "autheris:cdc:events";

    /// <summary>Received envelopes older than this are rejected (replay window).</summary>
    public static readonly TimeSpan MaxMessageAge = TimeSpan.FromSeconds(60);

    /// <summary>Received envelopes stamped further than this in the future are rejected (clock skew allowance).</summary>
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromSeconds(30);

    internal const int EnvelopeVersion = 1;
    internal const int MaxSeenNonces = 262_144;
    internal const int MaxEnvelopeBytes = 32 * 1024 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string AadLabel = "autheris-cdc-pubsub-v1";
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("autheris-cdc-pubsub-v1");
    private static readonly RedisChannel PubSubChannel = RedisChannel.Literal(PubSubChannelName);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly InMemoryCdcEventChannel _inner;
    private readonly ILogger<RedisCdcEventChannel> _logger;
    private readonly ICdcKeyEncryptionKeyProvider _keys;
    private readonly TimeProvider _time;
    private readonly string _instanceId;
    private readonly ISubscriber? _subscriber;
    private readonly Action<RedisChannel, RedisValue> _redisHandler;

    private readonly object _replayGate = new();
    private readonly HashSet<string> _seenNonces = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, long TimestampMs)> _seenOrder = new();
    private long _evictedWatermarkMs = long.MinValue;
    private int _disposed;

    public RedisCdcEventChannel(
        IConnectionMultiplexer multiplexer,
        InMemoryCdcEventChannel inner,
        ILogger<RedisCdcEventChannel> logger,
        ICdcKeyEncryptionKeyProvider? keyProvider,
        string? instanceId = null,
        TimeProvider? timeProvider = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
        _redisHandler = OnRedisMessageReceived;

        // Fail closed: never put unencrypted, unauthenticated CDC row images on the shared Redis channel.
        var activeKekId = keyProvider?.ActiveKekId;
        if (keyProvider is null || string.IsNullOrWhiteSpace(activeKekId) || !keyProvider.TryGetKek(activeKekId, out var kek) || kek.Length < KeySize)
        {
            const string message =
                "Security error: the cluster-wide CDC channel (Redis pub/sub '" + PubSubChannelName + "') requires a CDC key-encryption key " +
                "(Gateway:CdcStream:Encryption:ActiveKekId and Keks, each secret at least 32 bytes) to encrypt and authenticate the envelopes. " +
                "Refusing to start the Redis CDC channel without one.";
            _logger.LogCritical(message);
            throw new InvalidOperationException(message);
        }

        _keys = keyProvider;
        if (string.Equals(activeKekId, KeyVaultCdcKekProvider.EphemeralKekId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The Redis CDC channel uses the ephemeral development KEK; envelopes from other nodes cannot be authenticated and are dropped. Configure Gateway:CdcStream:Encryption for multi-node CDC.");
        }

        try
        {
            _subscriber = _multiplexer.GetSubscriber();
            _subscriber?.Subscribe(PubSubChannel, _redisHandler, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Redis CDC pub/sub channel '{Channel}'; falling back to local CDC channel.", PubSubChannelName);
        }
    }

    public string InstanceId => _instanceId;

    public async ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Always deliver to local subscribers immediately.
        await _inner.PublishAsync(cdcEvent, ct).ConfigureAwait(false);

        if (!_multiplexer.IsConnected)
        {
            _logger.LogDebug("Redis multiplexer is disconnected; CDC event '{EventId}' published locally only.", cdcEvent.EventId);
            return;
        }

        try
        {
            var subscriber = _subscriber ?? _multiplexer.GetSubscriber();
            if (subscriber is null)
            {
                return;
            }

            var kekId = _keys.ActiveKekId;
            if (string.IsNullOrWhiteSpace(kekId) || !_keys.TryGetKek(kekId, out var kek) || kek.Length < KeySize)
            {
                // Fail closed: the event is not sent in clear text; local subscribers already received it.
                _logger.LogError("No usable CDC KEK is available; CDC event '{EventId}' was not published to Redis channel '{Channel}'.", cdcEvent.EventId, PubSubChannelName);
                return;
            }

            var payload = Seal(cdcEvent, _instanceId, kekId, kek.Span, _time.GetUtcNow().ToUnixTimeMilliseconds());
            await subscriber.PublishAsync(PubSubChannel, payload, CommandFlags.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish CDC event '{EventId}' to Redis channel '{Channel}'; local subscribers already received the event.", cdcEvent.EventId, PubSubChannelName);
        }
    }

    public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _inner.SubscribeAsync(topic, ct);
    }

    private void OnRedisMessageReceived(RedisChannel channel, RedisValue message)
    {
        if (Volatile.Read(ref _disposed) != 0 || message.IsNullOrEmpty)
        {
            return;
        }

        try
        {
            var outcome = TryOpen(message, out var cdcEvent);
            if (outcome != OpenOutcome.Accepted)
            {
                if (outcome != OpenOutcome.SelfEcho)
                {
                    // Never log envelope content: it may carry row data.
                    _logger.LogWarning("Rejected CDC envelope on Redis channel '{Channel}': {Reason}.", PubSubChannelName, outcome);
                }

                return;
            }

            _ = DispatchRemoteEventAsync(cdcEvent!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process incoming CDC event from Redis channel '{Channel}'.", PubSubChannelName);
        }
    }

    private async Task DispatchRemoteEventAsync(CdcEvent cdcEvent)
    {
        try
        {
            await _inner.PublishAsync(cdcEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch remote CDC event '{EventId}' to local channel.", cdcEvent.EventId);
        }
    }

    internal enum OpenOutcome
    {
        Accepted,
        SelfEcho,
        Malformed,
        UnknownKey,
        Stale,
        AuthenticationFailed,
        Replayed
    }

    /// <summary>Authenticates, decrypts and replay-checks one received envelope.</summary>
    internal OpenOutcome TryOpen(RedisValue message, out CdcEvent? cdcEvent)
    {
        cdcEvent = null;
        var raw = (byte[]?)message;
        if (raw is null || raw.Length == 0 || raw.Length > MaxEnvelopeBytes)
        {
            return OpenOutcome.Malformed;
        }

        string kekId;
        string senderId;
        long timestampMs;
        string nonceText;
        byte[] nonce;
        byte[] ciphertext;
        byte[] tag;
        try
        {
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != EnvelopeVersion)
            {
                return OpenOutcome.Malformed;
            }

            kekId = RequiredString(root, "kid");
            senderId = RequiredString(root, "iid");
            if (!root.TryGetProperty("ts", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out timestampMs))
            {
                return OpenOutcome.Malformed;
            }

            nonceText = RequiredString(root, "n");
            nonce = Convert.FromBase64String(nonceText);
            ciphertext = Convert.FromBase64String(RequiredString(root, "c"));
            tag = Convert.FromBase64String(RequiredString(root, "t"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return OpenOutcome.Malformed;
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize || kekId.Length == 0 || senderId.Length == 0)
        {
            return OpenOutcome.Malformed;
        }

        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (timestampMs < nowMs - (long)MaxMessageAge.TotalMilliseconds || timestampMs > nowMs + (long)MaxFutureSkew.TotalMilliseconds)
        {
            return OpenOutcome.Stale;
        }

        if (!_keys.TryGetKek(kekId, out var kek) || kek.Length < KeySize)
        {
            return OpenOutcome.UnknownKey;
        }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            if (!TryDecrypt(kek.Span, nonce, ciphertext, tag, BuildAad(kekId, senderId, timestampMs, nonceText), plaintext))
            {
                return OpenOutcome.AuthenticationFailed;
            }

            // Authenticated: only now may the envelope touch the replay cache (unauthenticated junk cannot evict entries).
            if (string.Equals(senderId, _instanceId, StringComparison.Ordinal))
            {
                return OpenOutcome.SelfEcho;
            }

            if (!TryRegisterNonce(senderId + ":" + nonceText, timestampMs, nowMs))
            {
                return OpenOutcome.Replayed;
            }

            try
            {
                cdcEvent = DecodeEvent(plaintext);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException
                or ArgumentException or OverflowException or CdcPayloadCodecException)
            {
                return OpenOutcome.Malformed;
            }

            return OpenOutcome.Accepted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Bounded seen-nonce cache. Entries older than the replay window are purged; when the cache is full the oldest entry is
    /// evicted and its timestamp raises a watermark below which every envelope is rejected (an evicted nonce can never be replayed).
    /// </summary>
    internal bool TryRegisterNonce(string key, long timestampMs, long nowMs)
    {
        lock (_replayGate)
        {
            var expiry = nowMs - (long)MaxMessageAge.TotalMilliseconds;
            while (_seenOrder.Count > 0 && _seenOrder.Peek().TimestampMs < expiry)
            {
                _seenNonces.Remove(_seenOrder.Dequeue().Key);
            }

            if (timestampMs <= _evictedWatermarkMs || _seenNonces.Contains(key))
            {
                return false;
            }

            while (_seenNonces.Count >= MaxSeenNonces && _seenOrder.Count > 0)
            {
                var (oldKey, oldTs) = _seenOrder.Dequeue();
                _seenNonces.Remove(oldKey);
                _evictedWatermarkMs = Math.Max(_evictedWatermarkMs, oldTs);
            }

            _seenNonces.Add(key);
            _seenOrder.Enqueue((key, timestampMs));
            return true;
        }
    }

    /// <summary>Encrypts and authenticates one event into the pub/sub wire envelope (JSON).</summary>
    internal static string Seal(CdcEvent cdcEvent, string instanceId, string kekId, ReadOnlySpan<byte> kek, long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        var plaintext = EncodeEvent(cdcEvent);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var nonceText = Convert.ToBase64String(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, key, ReadOnlySpan<byte>.Empty, HkdfInfo);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, BuildAad(kekId, instanceId, timestampMs, nonceText));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var buffer = new ArrayBufferWriter<byte>(ciphertext.Length + 256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", EnvelopeVersion);
            w.WriteString("kid", kekId);
            w.WriteString("iid", instanceId);
            w.WriteNumber("ts", timestampMs);
            w.WriteString("n", nonceText);
            w.WriteString("c", Convert.ToBase64String(ciphertext));
            w.WriteString("t", Convert.ToBase64String(tag));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool TryDecrypt(ReadOnlySpan<byte> kek, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] aad, byte[] plaintext)
    {
        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, key, ReadOnlySpan<byte>.Empty, HkdfInfo);
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Length-prefixed (injective) associated data: label, version, KEK id, sender instance id, timestamp, nonce, channel.</summary>
    private static byte[] BuildAad(string kekId, string instanceId, long timestampMs, string nonceText)
    {
        var sb = new StringBuilder(AadLabel);
        sb.Append('|').Append(EnvelopeVersion.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, kekId);
        AppendField(sb, instanceId);
        sb.Append('|').Append(timestampMs.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, nonceText);
        AppendField(sb, PubSubChannelName);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendField(StringBuilder sb, string value) =>
        sb.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? throw new FormatException(name)
            : throw new FormatException(name);

    /// <summary>Type-preserving plaintext encoding of an event (row images through the closed-tag <see cref="CdcPayloadCodec"/>).</summary>
    internal static byte[] EncodeEvent(CdcEvent cdcEvent)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", cdcEvent.EventId);
            w.WriteString("d", cdcEvent.Table.Domain);
            w.WriteString("s", cdcEvent.Table.Schema);
            w.WriteString("tb", cdcEvent.Table.TableName);
            w.WriteNumber("op", (int)cdcEvent.Operation);
            w.WriteString("tn", cdcEvent.TenantId);
            w.WriteString("ts", cdcEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            WriteImage(w, "b", cdcEvent.Before);
            WriteImage(w, "a", cdcEvent.After);
            if (cdcEvent.Metadata is null)
            {
                w.WriteNull("md");
            }
            else
            {
                w.WriteStartObject("md");
                foreach (var (key, value) in cdcEvent.Metadata)
                {
                    w.WriteString(key, value);
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static CdcEvent DecodeEvent(byte[] utf8)
    {
        using var doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("root");
        }

        var operationValue = root.GetProperty("op").GetInt32();
        var operation = (CdcOperation)operationValue;
        if (!Enum.IsDefined(operation))
        {
            throw new FormatException("op");
        }

        var tenantElement = root.GetProperty("tn");
        IReadOnlyDictionary<string, string>? metadata = null;
        var md = root.GetProperty("md");
        if (md.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in md.EnumerateObject())
            {
                map[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }

            metadata = map;
        }
        else if (md.ValueKind != JsonValueKind.Null)
        {
            throw new FormatException("md");
        }

        return new CdcEvent(
            RequiredString(root, "id"),
            new TableIdentifier(RequiredString(root, "d"), RequiredString(root, "s"), RequiredString(root, "tb")),
            operation,
            tenantElement.ValueKind == JsonValueKind.Null ? null : tenantElement.GetString(),
            ReadImage(root, "b"),
            ReadImage(root, "a"),
            DateTimeOffset.Parse(RequiredString(root, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            metadata);
    }

    private static void WriteImage(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, object?>? row)
    {
        if (row is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteBase64String(name, CdcPayloadCodec.Instance.SerializeImage(CoerceToCodecTypes(row)));
    }

    private static IReadOnlyDictionary<string, object?>? ReadImage(JsonElement root, string name)
    {
        var el = root.GetProperty(name);
        if (el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var image = CdcPayloadCodec.Instance.DeserializeImage(el.GetBytesFromBase64());
        var result = new Dictionary<string, object?>(image.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in image)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>Values outside the codec tag set are sent as their invariant string form (the codec rejects unknown types).</summary>
    private static IReadOnlyDictionary<string, object?> CoerceToCodecTypes(IReadOnlyDictionary<string, object?> row)
    {
        var allSupported = true;
        foreach (var value in row.Values)
        {
            if (!IsCodecType(value))
            {
                allSupported = false;
                break;
            }
        }

        if (allSupported)
        {
            return row;
        }

        var copy = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
        foreach (var (key, value) in row)
        {
            copy[key] = IsCodecType(value) ? value : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return copy;
    }

    private static bool IsCodecType(object? value) => value is null or DBNull or string or long or int or short or byte or decimal
        or double or float or bool or DateTime or DateTimeOffset or DateOnly or TimeOnly or Guid or byte[];

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_subscriber is not null)
        {
            try
            {
                await _subscriber.UnsubscribeAsync(PubSubChannel, _redisHandler, CommandFlags.FireAndForget).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while unsubscribing from Redis CDC channel '{Channel}'.", PubSubChannelName);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_subscriber is not null)
        {
            try
            {
                _subscriber.Unsubscribe(PubSubChannel, _redisHandler, CommandFlags.FireAndForget);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while unsubscribing from Redis CDC channel '{Channel}'.", PubSubChannelName);
            }
        }
    }
}
