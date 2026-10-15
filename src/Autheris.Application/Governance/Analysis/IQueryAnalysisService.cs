namespace Autheris.Application.Governance.Analysis;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Issue #79: Static query analysis and access requirements service.
/// Analyzes GraphQL queries, SQL queries, declarative SQL endpoints, and stored procedures against the governance catalog
/// and consent policies without ever connecting to or reading from any underlying data source.
/// </summary>
public interface IQueryAnalysisService
{
    /// <summary>
    /// Analyzes a batch of queries for the target principal (or the caller if <paramref name="targetPrincipal"/> is null)
    /// without executing any source database reads.
    /// </summary>
    Task<QueryAnalysisResult> AnalyzeQueriesAsync(
        IReadOnlyList<QueryAnalysisItem> queries,
        ClaimsPrincipal callerPrincipal,
        ClaimsPrincipal? targetPrincipal = null,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates access requirements for a batch of queries across a group of principals and returns only aggregate counts
    /// per dataset (never individual user names or SIDs).
    /// </summary>
    Task<IReadOnlyList<DatasetGrantGapSummary>> EvaluateGroupAccessRequirementsAsync(
        IReadOnlyList<QueryAnalysisItem> queries,
        IReadOnlyList<ClaimsPrincipal> groupMembers,
        ClaimsPrincipal callerPrincipal,
        CancellationToken ct = default);
}

/// <summary>
/// Issue #79: Splits a bundled multi-table consent request by Data Owner so each Data Owner receives an approval ticket
/// containing only their own tables while sharing the caller's <c>ConsumerReference</c>.
/// </summary>
public interface IBundledConsentRequestService
{
    Task<BundledConsentRequestResult> SubmitBundledRequestAsync(
        ClaimsPrincipal requester,
        BundledConsentRequest request,
        CancellationToken ct = default);
}
