namespace Autheris.Application.Security.Rebac.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-SEC-04: Batch DataLoader for ReBAC checks.
/// Eliminates N+1 queries during GraphQL list evaluation by de-duplicating and bulk-executing tuple queries.
/// </summary>
public interface IRebacBatchDataLoader
{
    void Enqueue(RebacCheckRequest request);

    ValueTask<IReadOnlyDictionary<RebacCheckRequest, bool>> ExecuteBatchAsync(string tenantId, CancellationToken ct = default);

    ValueTask<bool> CheckAsync(string tenantId, string user, string relation, string targetObject, CancellationToken ct = default);
}
