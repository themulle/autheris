using System;
using System.Text.Json;
using Autheris.Application.Caching.Interfaces;
using MemoryPack;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Serialization;

/// <summary>
/// High-performance binary cache serializer utilizing MemoryPack (C# 12/13 Source Generators)
/// with zero-allocation slicing and automated JSON fallback.
/// Magic byte format:
///   [0x4D] ('M') -> MemoryPack format
///   [0x4A] ('J') -> System.Text.Json UTF-8 format
/// </summary>
public sealed class MemoryPackCacheSerializer : IBinaryCacheSerializer
{
    private const byte MagicMemoryPack = 0x4D; // 'M'
    private const byte MagicJson = 0x4A;       // 'J'

    private readonly ILogger<MemoryPackCacheSerializer>? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MemoryPackCacheSerializer(ILogger<MemoryPackCacheSerializer>? logger = null)
    {
        _logger = logger;
    }

    public byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            return Array.Empty<byte>();
        }

        try
        {
            var mpBytes = MemoryPackSerializer.Serialize(value);
            var result = new byte[mpBytes.Length + 1];
            result[0] = MagicMemoryPack;
            Buffer.BlockCopy(mpBytes, 0, result, 1, mpBytes.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MemoryPack serialization unavailable for {Type}, falling back to UTF-8 JSON.", typeof(T).Name);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var result = new byte[jsonBytes.Length + 1];
            result[0] = MagicJson;
            Buffer.BlockCopy(jsonBytes, 0, result, 1, jsonBytes.Length);
            return result;
        }
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return default;
        }

        byte magic = bytes[0];
        ReadOnlySpan<byte> payload = bytes[1..];

        if (magic == MagicMemoryPack)
        {
            try
            {
                return MemoryPackSerializer.Deserialize<T>(payload);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MemoryPack deserialization failed for {Type}.", typeof(T).Name);
                return default;
            }
        }

        if (magic == MagicJson)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(payload, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "JSON fallback deserialization failed for {Type}.", typeof(T).Name);
                return default;
            }
        }

        // Legacy / untagged payloads: attempt MemoryPack first, then JSON
        try
        {
            return MemoryPackSerializer.Deserialize<T>(bytes);
        }
        catch
        {
            try
            {
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Untagged deserialization failed for {Type}.", typeof(T).Name);
                return default;
            }
        }
    }

    public bool TryDeserialize<T>(ReadOnlySpan<byte> bytes, out T? result)
    {
        try
        {
            result = Deserialize<T>(bytes);
            return result != null;
        }
        catch
        {
            result = default;
            return false;
        }
    }
}
