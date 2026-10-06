namespace Autheris.Application.OData.Services;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.OData.Interfaces;

/// <summary>
/// High-performance in-memory cache manager storing pre-serialized OpenAPI UTF-8 byte payloads (F-API-03).
/// </summary>
public sealed class OpenApiCacheManager : IOpenApiCacheManager
{
    private const int MaxCacheEntries = 200;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<byte[]> GetOrAddAsync(
        string? domainScope,
        bool isYaml,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default)
    {
        return GetOrAddAsync(domainScope, isYaml, isModular: false, factory, ct);
    }

    public async Task<byte[]> GetOrAddAsync(
        string? domainScope,
        bool isYaml,
        bool isModular,
        Func<CancellationToken, Task<string>> factory,
        CancellationToken ct = default)
    {
        var key = BuildKey(domainScope, isYaml, isModular);
        if (key is null)
        {
            // G3: non-identifier / oversized domain names are never cached (no key collisions, no unbounded keys).
            var uncached = await factory(ct).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(uncached);
        }

        if (_cache.TryGetValue(key, out var cachedBytes))
        {
            return cachedBytes;
        }

        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        var contentString = await factory(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(contentString);
        _cache[key] = bytes;
        return bytes;
    }

    public void InvalidateCache()
    {
        _cache.Clear();
    }

    internal static string? BuildKey(string? domainScope, bool isYaml, bool isModular)
    {
        var format = isYaml ? "yaml" : "json";
        var mod = isModular ? "_modular" : "";
        if (string.IsNullOrWhiteSpace(domainScope))
        {
            return $"global_{format}{mod}";
        }

        // G3: key on the exact domain. Dropping characters (previous behaviour) mapped e.g. "a.b" and "ab" to one entry.
        if (domainScope.Length > 64 || !domainScope.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
        {
            return null;
        }

        return $"domain:{domainScope.ToLowerInvariant()}_{format}{mod}";
    }
}
