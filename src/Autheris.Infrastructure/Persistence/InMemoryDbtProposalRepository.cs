namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public sealed class InMemoryDbtProposalRepository : IDbtProposalRepository
{
    private readonly ConcurrentDictionary<Guid, DbtMetadataProposal> _proposals = new();

    public Task<DbtMetadataProposal> AddProposalAsync(DbtMetadataProposal proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _proposals[proposal.Id] = proposal;
        return Task.FromResult(proposal);
    }

    public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default)
    {
        var snapshot = _proposals.Values.ToArray();
        var query = snapshot.Where(p => p.Status == DbtProposalStatus.PendingReview);
        if (table.HasValue)
        {
            query = query.Where(p => p.Table == table.Value);
        }
        return Task.FromResult<IReadOnlyList<DbtMetadataProposal>>(query.ToList());
    }

    public Task<DbtMetadataProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default)
    {
        _proposals.TryGetValue(proposalId, out var proposal);
        return Task.FromResult(proposal);
    }

    public Task<DbtMetadataProposal> UpdateProposalStatusAsync(Guid proposalId, DbtProposalStatus status, string reviewedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedBy);

        if (!_proposals.TryGetValue(proposalId, out var existing))
        {
            throw new KeyNotFoundException($"Dbt proposal with ID '{proposalId}' not found.");
        }

        var updated = existing with
        {
            Status = status,
            ReviewedAt = DateTimeOffset.UtcNow,
            ReviewedBy = reviewedBy
        };

        _proposals.AddOrUpdate(proposalId, updated, (_, _) => updated);
        return Task.FromResult(updated);
    }
}
