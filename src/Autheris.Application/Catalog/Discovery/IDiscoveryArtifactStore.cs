namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public interface IDiscoveryArtifactStore
{
    Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default);

    async Task<DiscoveryArtifacts?> GetAsync(Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default)
    {
        var all = await GetAllAsync(tableId, ct).ConfigureAwait(false);
        return all.FirstOrDefault(a => a.Provenance == provenance);
    }

    async Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(Guid tableId, CancellationToken ct = default)
    {
        var item = await GetAsync(tableId, ct).ConfigureAwait(false);
        return item is null ? [] : [item];
    }

    Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default) =>
        DeleteAsync(tableId, actor, ct);
}
