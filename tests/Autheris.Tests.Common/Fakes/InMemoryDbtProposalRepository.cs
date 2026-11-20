using Autheris.Application.Catalog.Semantic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IDbtProposalRepository"/> for unit tests.
/// </summary>
public sealed class InMemoryDbtProposalRepository : IDbtProposalRepository
{
    private readonly object _gate = new();
    private readonly List<DbtMetadataProposal> _proposals = [];

    public InMemoryDbtProposalRepository(params DbtMetadataProposal[] seed)
    {
        foreach (var p in seed)
        {
            _proposals.Add(p);
        }
    }

    public IReadOnlyList<DbtMetadataProposal> Proposals
    {
        get
        {
            lock (_gate)
            {
                return _proposals.ToArray();
            }
        }
    }

    public InMemoryDbtProposalRepository Add(DbtMetadataProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            _proposals.Add(proposal);
        }

        return this;
    }

    public Task<DbtMetadataProposal> AddProposalAsync(DbtMetadataProposal proposal, CancellationToken ct = default)
    {
        Add(proposal);
        return Task.FromResult(proposal);
    }

    public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var result = _proposals
                .Where(p => p.Status == DbtProposalStatus.PendingReview && (table is null || p.Table == table.Value))
                .ToList();
            return Task.FromResult<IReadOnlyList<DbtMetadataProposal>>(result);
        }
    }

    public Task<DbtMetadataProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_proposals.FirstOrDefault(p => p.Id == proposalId));
        }
    }

    public Task<DbtMetadataProposal> UpdateProposalStatusAsync(
        Guid proposalId,
        DbtProposalStatus status,
        string reviewedBy,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var idx = _proposals.FindIndex(p => p.Id == proposalId);
            if (idx < 0)
            {
                throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
            }

            var updated = _proposals[idx] with
            {
                Status = status,
                ReviewedBy = reviewedBy,
                ReviewedAt = DateTimeOffset.UtcNow
            };
            _proposals[idx] = updated;
            return Task.FromResult(updated);
        }
    }
}
