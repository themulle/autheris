namespace Autheris.Application.Security.Rebac.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-SEC-04: Storage provider for ReBAC relationship tuples (Google Zanzibar / OpenFGA).
/// Manages (TenantId, User, Relation, Object) tuples with strict multi-tenant partitioning.
/// </summary>
public interface IRebacStore
{
    ValueTask AddTupleAsync(RebacTuple tuple, CancellationToken ct = default);
    ValueTask AddTuplesAsync(IEnumerable<RebacTuple> tuples, CancellationToken ct = default);
    ValueTask<bool> DeleteTupleAsync(RebacTuple tuple, CancellationToken ct = default);
    ValueTask<IReadOnlyList<RebacTuple>> GetTuplesAsync(
        string tenantId,
        string? user = null,
        string? relation = null,
        string? obj = null,
        CancellationToken ct = default);
    ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default);
}
