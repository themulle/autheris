namespace Autheris.Application.Catalog.Discovery;

using Autheris.Domain.Model;
using Autheris.Domain.Security;

public interface IDiscoveryArtifactStore
{
    Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default);
    Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default);
}
