namespace Autheris.Application.Security.Rebac.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-SEC-04: High-performance ReBAC evaluation engine.
/// Evaluates entity-to-entity and user-to-object relationships with graph traversal and DataLoader batching.
/// </summary>
public interface IRebacEvaluator
{
    bool IsEnabled { get; }

    ValueTask<RebacCheckResult> CheckAsync(RebacCheckRequest request, CancellationToken ct = default);

    ValueTask<RebacBatchCheckResult> BatchCheckAsync(RebacBatchCheckRequest request, CancellationToken ct = default);

    void RegisterInheritance(string baseRelation, params string[] inheritedBy);

    void InvalidateTenantCache(string tenantId);
}
