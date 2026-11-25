namespace Autheris.Application.Governance.Declarative;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>Tenant-scoped store for declarative manifest proposals (<c>governance_manifest_proposals</c>).</summary>
public interface IManifestProposalRepository
{
    /// <summary>
    /// Stores a new pending proposal and, in the same transaction, marks every pending proposal of the same tenant and scope as
    /// <see cref="ManifestProposalStatus.Superseded"/>. Returns the ids of the superseded proposals.
    /// </summary>
    Task<IReadOnlyList<Guid>> CreateAsync(ManifestProposal proposal, CancellationToken ct = default);

    Task<ManifestProposal?> GetAsync(TenantId tenantId, Guid id, CancellationToken ct = default);

    /// <summary>
    /// Moves the proposal from <paramref name="from"/> to <paramref name="to"/> only when it is still in <paramref name="from"/> (the first decision
    /// wins). Returns <see langword="false"/> when another decision came first.
    /// </summary>
    Task<bool> TryTransitionAsync(
        TenantId tenantId,
        Guid id,
        ManifestProposalStatus from,
        ManifestProposalStatus to,
        Sid? decidedBy,
        DateTimeOffset? decidedAt,
        string? rejectReason,
        CancellationToken ct = default);

    /// <summary>Replaces the diff and the state hash of a pending proposal (after a stale confirmation). False when it is no longer pending.</summary>
    Task<bool> UpdateDiffAsync(TenantId tenantId, Guid id, string diffJson, string stateHash, CancellationToken ct = default);

    /// <summary>Pending proposals of every tenant whose lifetime ended at <paramref name="now"/>, oldest first, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<ManifestProposal>> ListExpiredPendingAsync(DateTimeOffset now, int max, CancellationToken ct = default);
}
