namespace Autheris.Application.Dbt.Interfaces;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The authenticated caller of a direct dbt governance import. Virtual filters and access profiles are saved as
/// <see cref="Actor"/> (never as the sync, so the approval workflow and the managed-resource lock apply) in <see cref="Tenant"/>,
/// the server-resolved request tenant. A payload tenant other than <see cref="Tenant"/> is accepted only when
/// <see cref="IsClusterAdmin"/> (canonical ClusterAdmin) is set.
/// </summary>
public sealed record DbtGovernanceImportCaller(VirtualFilterActor Actor, TenantId Tenant, bool IsClusterAdmin = false);

public interface IDbtMetadataIngestionService
{
    Task<DbtSyncResult> IngestManifestStreamAsync(Stream manifestStream, DbtGovernanceImportCaller caller, bool dryRun = false, CancellationToken ct = default);
    Task<DbtSyncResult> IngestManifestFileAsync(string filePath, DbtGovernanceImportCaller caller, bool dryRun = false, CancellationToken ct = default);
    Task<DbtGovernanceSyncResult> IngestGovernanceStreamAsync(Stream governanceStream, DbtGovernanceImportCaller caller, bool dryRun = false, bool replace = false, CancellationToken ct = default);
    Task<DbtGovernanceSyncResult> IngestGovernanceFileAsync(string filePath, DbtGovernanceImportCaller caller, bool dryRun = false, bool replace = false, CancellationToken ct = default);
    Task<DbtMetadataProposal> ApproveProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default);
    Task<DbtMetadataProposal> RejectProposalAsync(Guid proposalId, string reviewedBy, CancellationToken ct = default);
    Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default);
}

