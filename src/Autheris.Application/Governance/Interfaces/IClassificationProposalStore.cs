namespace Autheris.Application.Governance.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Repository store managing persistence, retrieval, and state transitions
/// of table classification proposals across the governance lifecycle.
/// </summary>
public interface IClassificationProposalStore
{
    /// <summary>
    /// Saves or updates a table classification proposal.
    /// </summary>
    Task SaveProposalAsync(TableClassificationProposal proposal, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a proposal by its unique ID.
    /// </summary>
    Task<TableClassificationProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves pending proposals awaiting review, optionally filtered by reviewer role or owner SID.
    /// </summary>
    Task<IReadOnlyList<TableClassificationProposal>> GetPendingProposalsAsync(
        string? reviewerRole = null,
        Sid? ownerSid = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most recent proposal for a specific table.
    /// </summary>
    Task<TableClassificationProposal?> GetProposalByTableAsync(TableIdentifier table, CancellationToken ct = default);

    /// <summary>
    /// Updates the data owner on an existing proposal for a given table.
    /// </summary>
    Task<bool> UpdateDataOwnerAsync(TableIdentifier table, string dataOwnerSid, CancellationToken ct = default);
}
