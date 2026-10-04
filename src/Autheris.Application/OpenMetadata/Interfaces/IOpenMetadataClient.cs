using Autheris.Application.OpenMetadata.Models;

namespace Autheris.Application.OpenMetadata.Interfaces;

public interface IOpenMetadataClient
{
    Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default);
    Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default);
}
