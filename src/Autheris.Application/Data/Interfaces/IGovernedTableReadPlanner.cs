namespace Autheris.Application.Data.Interfaces;

using System.Collections.Generic;
using Autheris.Application.Analytics;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// WP-ARC-B-01: a transport-neutral table-read intent. The planner turns it into Trino-dialect SQL text for the governed
/// compile path (<c>IGovernedSqlExecutionService</c>); every adapter (REST datasets, GraphQL flat reads, OData, MCP, connector
/// SPI) states what it wants to read and never builds SQL text itself.
/// </summary>
/// <param name="Table">The catalog table to read.</param>
/// <param name="SelectColumns">The requested columns (catalog spelling or request spelling); <c>null</c> or empty selects every column the decision does not deny.</param>
/// <param name="Filter">Neutral filter; its predicate is rendered through <see cref="TableFilterClause.GetTrinoPredicate"/>.</param>
/// <param name="OrderBy">Requested ordering; empty falls back to the primary key.</param>
/// <param name="Limit">Number of rows the statement fetches (the caller adds its own look-ahead row); must be positive.</param>
/// <param name="Offset">Rows to skip in the database (Trino <c>OFFSET n ROWS</c>); zero or negative emits no OFFSET.</param>
/// <param name="Transport">Transport name carried to the governed request (see <c>SqlTransports</c>).</param>
public sealed record TableReadIntent(
    TableIdentifier Table,
    IReadOnlyList<string>? SelectColumns,
    TableFilterClause? Filter,
    IReadOnlyList<TableOrderBy>? OrderBy,
    int Limit,
    int Offset = 0,
    string? Transport = null);

/// <summary>
/// The planned statement: Trino text, the client parameters its markers name, and the columns the statement selects (in
/// the spelling the caller requested). <paramref name="Ordering"/> is the rule behind the ORDER BY (internal; never serialized to a caller).
/// </summary>
public sealed record PlannedTableRead(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> ColumnsToSelect,
    DefaultOrderingKind Ordering = DefaultOrderingKind.Explicit);

/// <summary>
/// Transport-neutral governed aggregation intent (#28, #37).
/// </summary>
public sealed record TableAggregateIntent(
    TableIdentifier Table,
    IReadOnlyList<string>? GroupBy,
    IReadOnlyList<AggregateSelection>? Aggregates,
    TableFilterClause? Filter,
    IReadOnlyList<TableOrderBy>? OrderBy,
    int Limit,
    string? Transport = null,
    IReadOnlyList<TimeBucketSpecification>? TimeBuckets = null);

/// <summary>
/// Planned Trino SQL statement for a governed aggregation query (#28, #37).
/// </summary>
public sealed record PlannedTableAggregate(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> GroupByColumns,
    IReadOnlyList<AggregateSelection> Aggregates,
    string CountColumnAlias);

/// <summary>
/// WP-ARC-B-01: shared governed table-read planner. Authorizes columns against the <see cref="TableAccessDecision"/>, guards
/// filter and order columns against oracle inference, and formulates the SQL. Fail-closed: an unknown column, a denied or
/// masked filter/order column or an unsupported filter throws; no best-effort text is ever produced.
/// </summary>
public interface IGovernedTableReadPlanner
{
    /// <summary>Plans a page read.</summary>
    /// <exception cref="System.UnauthorizedAccessException">The decision denies the table, or no column is accessible.</exception>
    /// <exception cref="System.ArgumentException">A column is unknown, denied, or masked where clear text is required.</exception>
    PlannedTableRead Plan(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision);

    /// <summary>
    /// Plans <c>SELECT COUNT(*)</c> over the same table and filter (the intent's columns, order and pagination are ignored).
    /// The single result column holds the total.
    /// </summary>
    PlannedTableRead PlanCount(TableReadIntent intent, TableMetadata metadata, TableAccessDecision decision);

    /// <summary>
    /// Plans a governed <c>SELECT ... GROUP BY ...</c> statement with mandatory per-group <c>COUNT(*)</c> for minimum group size guarding (#28, #37).
    /// Rejects any masked or denied column in <c>GROUP BY</c>, aggregate arguments, filter, or order clauses.
    /// </summary>
    PlannedTableAggregate PlanAggregate(TableAggregateIntent intent, TableMetadata metadata, TableAccessDecision decision);

    /// <summary>
    /// The text of an unfiltered, unpaged <c>SELECT *</c> over the table (Arrow export of a whole table). Like every planner text it is
    /// Trino text for the governed compile path, which applies the policy.
    /// </summary>
    string BuildFullTableSelect(TableIdentifier table);

    /// <summary>
    /// PLAN-DATA-178: Plans a bulk keyset streaming query against a single table using monotonic keyset cursor seek.
    /// Rejects joins, subqueries, and raw SQL injection.
    /// </summary>
    PlannedTableRead PlanKeysetStream(
        BulkTableStreamRequest request,
        TableMetadata metadata,
        TableAccessDecision decision);
}

