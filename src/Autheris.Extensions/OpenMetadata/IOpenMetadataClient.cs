using Autheris.Application.Catalog.OpenMetadata;

namespace Autheris.Extensions.OpenMetadata;

public interface IOpenMetadataClient
{
    Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default);
    Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default);
}
