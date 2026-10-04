namespace Autheris.Application.SchemaRegistry;

public interface ISchemaRegistryService
{
    Task<SchemaRegistrationResponse> RegisterSchemaAsync(SchemaRegistrationRequest request, CancellationToken cancellationToken = default);
    Task<SchemaDiffResult> CheckSchemaAsync(string serviceName, string targetSdl, CancellationToken cancellationToken = default);
    Task<RegisteredSchema?> GetLatestSchemaAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RegisteredSchema>> GetSchemaHistoryAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAllServicesAsync(CancellationToken cancellationToken = default);
}
