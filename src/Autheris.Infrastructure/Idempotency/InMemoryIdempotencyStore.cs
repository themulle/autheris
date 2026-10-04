using System.Collections.Concurrent;
using System.Text.Json;
using Autheris.Application.Interfaces;

namespace Autheris.Infrastructure.Idempotency;

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Entry(string Json, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                var val = JsonSerializer.Deserialize<T>(entry.Json, _jsonOptions);
                return Task.FromResult(val);
            }
            _store.TryRemove(key, out _);
        }
        return Task.FromResult<T?>(null);
    }

    public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        // Cleanup if size gets large
        if (_store.Count > 10000)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var kvp in _store)
            {
                if (kvp.Value.ExpiresAt <= now)
                {
                    _store.TryRemove(kvp.Key, out _);
                }
            }
        }

        var json = JsonSerializer.Serialize(value, _jsonOptions);
        var entry = new Entry(json, DateTimeOffset.UtcNow.Add(ttl));
        var added = _store.TryAdd(key, entry);
        return Task.FromResult(added);
    }
}
