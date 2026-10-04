namespace Autheris.Application.SchemaRegistry;

public interface ISchemaRegistryRepository
{
    Task<RegisteredSchema?> GetLatestAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegisteredSchema>> GetHistoryAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAllServicesAsync(CancellationToken cancellationToken = default);
    Task SaveSchemaAsync(RegisteredSchema schema, CancellationToken cancellationToken = default);
}
