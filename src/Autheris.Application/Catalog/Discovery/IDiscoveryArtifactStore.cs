namespace Autheris.Application.Catalog.Discovery;

using Autheris.Domain.Model;
using Autheris.Domain.Security;

public interface IDiscoveryArtifactStore
{
    Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default);

    /// <summary>Issue #1047: the artifacts of several tables (a discovery page); tables without artifacts are absent.</summary>
    async Task<IReadOnlyDictionary<Guid, DiscoveryArtifacts>> GetManyAsync(IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DiscoveryArtifacts>();
        foreach (var tableId in tableIds.Distinct())
        {
            if (await GetAsync(tableId, ct).ConfigureAwait(false) is { } artifacts)
            {
                result[tableId] = artifacts;
            }
        }

        return result;
    }
    Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default);
}
