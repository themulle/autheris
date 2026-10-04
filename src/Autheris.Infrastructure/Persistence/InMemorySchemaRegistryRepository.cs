namespace Autheris.Infrastructure.Persistence;

using System.Collections.Concurrent;
using Autheris.Application.SchemaRegistry;

public sealed class InMemorySchemaRegistryRepository : ISchemaRegistryRepository
{
    private readonly ConcurrentDictionary<string, List<RegisteredSchema>> _store = new(StringComparer.OrdinalIgnoreCase);

    public Task<RegisteredSchema?> GetLatestAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(serviceName, out var history))
        {
            lock (history)
            {
                var latest = history.LastOrDefault(s => s.IsActive);
                return Task.FromResult(latest);
            }
        }
        return Task.FromResult<RegisteredSchema?>(null);
    }

    public Task<IReadOnlyList<RegisteredSchema>> GetHistoryAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(serviceName, out var history))
        {
            lock (history)
            {
                return Task.FromResult<IReadOnlyList<RegisteredSchema>>(history.ToList());
            }
        }
        return Task.FromResult<IReadOnlyList<RegisteredSchema>>(Array.Empty<RegisteredSchema>());
    }

    public Task<IReadOnlyList<string>> GetAllServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(_store.Keys.ToList());
    }

    public Task SaveSchemaAsync(RegisteredSchema schema, CancellationToken cancellationToken = default)
    {
        var history = _store.GetOrAdd(schema.ServiceName, _ => new List<RegisteredSchema>());
        lock (history)
        {
            if (schema.IsActive)
            {
                foreach (var existing in history)
                {
                    existing.IsActive = false;
                }
            }
            history.Add(schema);
        }
        return Task.CompletedTask;
    }
}
