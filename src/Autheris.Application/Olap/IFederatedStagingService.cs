namespace Autheris.Application.Olap;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Domain.Model;
using TrinoSqlEngine.Analysis;

/// <summary>Issue #124: Key-set pushdown plan for cross-source equi-join dependency.</summary>
public sealed record KeySetPlanInfo(
    string DrivingStagingName,
    TableIdentifier DrivingTableIdentifier,
    string DrivingKeyColumn,
    string DrivenKeyColumn);

public sealed record StagingTableRequest(
    TableAccessTarget Reference,
    TableMetadata Metadata,
    TableAccessDecision Decision,
    string StagingName,
    IReadOnlyList<string> Projection,
    TableFilterClause? PushdownFilter = null,
    VirtualFilterTablePlan? VirtualFilterPlan = null,
    PushedParameterAudit? ParameterAudit = null,
    KeySetPlanInfo? KeySetPlan = null);

public sealed record FederationBudget(
    int MaxTableCount,
    int MaxStagedRowsPerTable,
    int MaxTotalStagedRows,
    long MaxStagedBytesPerTable,
    long MaxTotalStagedBytes,
    int TimeoutSeconds,
    int MaxParallelSourceReads,
    string? AuditCorrelationId = null);

public interface IFederatedStagingService
{
    Task<IReadOnlyList<OlapTableSource>> StageAsync(
        IReadOnlyList<StagingTableRequest> tables,
        ClaimsPrincipal user,
        TenantId tenantId,
        FederationBudget budget,
        CancellationToken ct = default);
}
