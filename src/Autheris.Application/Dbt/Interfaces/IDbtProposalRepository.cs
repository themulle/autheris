namespace Autheris.Application.Dbt.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IDbtProposalRepository
{
    Task<DbtMetadataProposal> AddProposalAsync(DbtMetadataProposal proposal, CancellationToken ct = default);
    Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default);
    Task<DbtMetadataProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default);
    Task<DbtMetadataProposal> UpdateProposalStatusAsync(Guid proposalId, DbtProposalStatus status, string reviewedBy, CancellationToken ct = default);
}
