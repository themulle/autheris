namespace Autheris.Tests.Integration;

using Autheris.Application.Common.Interfaces;
using Autheris.Infrastructure.Idempotency;

/// <summary>Test-only: a single in-process host stands in for a cluster, so its store may carry the cluster-wide marker.</summary>
internal sealed class ClusterWideTestIdempotencyStore : IClusterWideIdempotencyStore
{
    private readonly InMemoryIdempotencyStore _inner = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class => _inner.GetAsync<T>(key, ct);

    public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class =>
        _inner.SetIfNotExistsAsync(key, value, ttl, ct);
}
