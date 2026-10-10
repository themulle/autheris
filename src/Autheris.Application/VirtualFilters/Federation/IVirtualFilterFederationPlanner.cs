namespace Autheris.Application.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Services;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// F-GOV-14 (M5): plans, for every protected HTTP table of a WebSQL cross-source query, how the authorized keys are
/// loaded and enforced. The (filter, binding) pairs come only from the access decisions (single policy decision
/// point). Every failure throws a <see cref="Autheris.Domain.Exceptions.GatewaySecurityException"/> with a
/// <see cref="VirtualFilterErrorCodes"/> code; a table with applied Virtual Filters never gets "no plan".
/// </summary>
public interface IVirtualFilterFederationPlanner
{
    /// <param name="statement">The parsed user statement (null when it is not available); the query shape only ever relaxes what is fetched.</param>
    /// <param name="tables">The resolved tables of the query (a table may occur several times).</param>
    /// <param name="decisions">The access decision of every table, keyed by source-qualified identifier.</param>
    /// <param name="tenant">The caller's tenant.</param>
    /// <param name="ct">Cancellation; the request timeout covers key loading (SEC-12).</param>
    /// <returns>One plan per protected HTTP table, keyed by source-qualified identifier.</returns>
    Task<IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>> PlanAsync(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        TenantId tenant,
        CancellationToken ct);
}
