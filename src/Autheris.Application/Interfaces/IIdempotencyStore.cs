namespace Autheris.Application.Interfaces;

public interface IIdempotencyStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class;
}
