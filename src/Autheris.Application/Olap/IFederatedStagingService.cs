namespace Autheris.Application.Olap;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Analysis;

public sealed record StagingTableRequest(
    TableAccessTarget Reference,
    TableMetadata Metadata,
    TableAccessDecision Decision,
    string StagingName,
    IReadOnlyList<string> Projection,
    TableFilterClause? PushdownFilter = null);

public sealed record FederationBudget(
    int MaxTableCount,
    int MaxStagedRowsPerTable,
    int MaxTotalStagedRows,
    long MaxStagedBytesPerTable,
    long MaxTotalStagedBytes,
    int TimeoutSeconds,
    int MaxParallelSourceReads);

public interface IFederatedStagingService
{
    Task<IReadOnlyList<OlapTableSource>> StageAsync(
        IReadOnlyList<StagingTableRequest> tables,
        ClaimsPrincipal user,
        TenantId tenantId,
        FederationBudget budget,
        CancellationToken ct = default);
}
