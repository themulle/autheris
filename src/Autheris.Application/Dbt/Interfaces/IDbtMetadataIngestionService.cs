namespace Autheris.Application.Dbt.Interfaces;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IDbtMetadataIngestionService
{
    Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, bool dryRun = false, CancellationToken ct = default);
    Task<DbtMetadataProposal> ApproveProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default);
    Task<DbtMetadataProposal> RejectProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default);
    Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default);
}

