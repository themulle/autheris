namespace Autheris.Application.Caching.Interfaces;

/// <summary>
/// High-performance binary serialization abstraction for L2 cache storage (MemoryPack with JSON fallback).
/// Provides zero-allocation serialization and deserialization for hot-path caching.
/// </summary>
public interface IBinaryCacheSerializer
{
    /// <summary>
    /// Serializes an object to a binary byte array.
    /// </summary>
    byte[] Serialize<T>(T value);

    /// <summary>
    /// Deserializes a binary span into the target type.
    /// </summary>
    T? Deserialize<T>(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Attempts to deserialize without throwing exceptions on corrupted or invalid payloads.
    /// </summary>
    bool TryDeserialize<T>(ReadOnlySpan<byte> bytes, out T? result);
}
