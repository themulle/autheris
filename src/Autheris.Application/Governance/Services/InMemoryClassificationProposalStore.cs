namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IClassificationProposalStore"/>.
/// Supports dual-sign-off workflows, reviewer inboxes, and owner assignments.
/// </summary>
public sealed class InMemoryClassificationProposalStore : IClassificationProposalStore
{
    private readonly ConcurrentDictionary<Guid, TableClassificationProposal> _proposals = new();

    public Task SaveProposalAsync(TableClassificationProposal proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _proposals[proposal.ProposalId] = proposal;
        return Task.CompletedTask;
    }

    public Task<TableClassificationProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default)
    {
        _proposals.TryGetValue(proposalId, out var proposal);
        return Task.FromResult(proposal);
    }

    public Task<IReadOnlyList<TableClassificationProposal>> GetPendingProposalsAsync(
        string? reviewerRole = null,
        Sid? ownerSid = null,
        CancellationToken ct = default)
    {
        IEnumerable<TableClassificationProposal> query = _proposals.Values;

        if (string.Equals(reviewerRole, "DATA_OWNER", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => string.Equals(p.Status, "PENDING_OWNER_REVIEW", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(p.Status, "UNCLASSIFIED", StringComparison.OrdinalIgnoreCase));
        }
        else if (string.Equals(reviewerRole, "DATA_GOVERNANCE_REVIEWER", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p => string.Equals(p.Status, "PENDING_GOVERNANCE_REVIEW", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // All pending (not yet finalized or rejected)
            query = query.Where(p => !string.Equals(p.Status, "CLASSIFIED", StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(p.Status, "REJECTED", StringComparison.OrdinalIgnoreCase));
        }

        if (ownerSid.HasValue && !string.IsNullOrWhiteSpace(ownerSid.Value.Value))
        {
            query = query.Where(p => string.Equals(p.DataOwnerSid, ownerSid.Value.Value, StringComparison.OrdinalIgnoreCase));
        }

        var results = (IReadOnlyList<TableClassificationProposal>)query
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<TableClassificationProposal?> GetProposalByTableAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var proposal = _proposals.Values
            .Where(p => p.TableIdentifier.Equals(table))
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(proposal);
    }

    public Task<bool> UpdateDataOwnerAsync(TableIdentifier table, string dataOwnerSid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataOwnerSid);

        var existing = _proposals.Values
            .Where(p => p.TableIdentifier.Equals(table))
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefault();

        if (existing == null)
        {
            return Task.FromResult(false);
        }

        var updated = existing with
        {
            DataOwnerSid = dataOwnerSid,
            Status = existing.Status == "UNCLASSIFIED" ? "PENDING_OWNER_REVIEW" : existing.Status
        };

        _proposals[existing.ProposalId] = updated;
        return Task.FromResult(true);
    }
}
