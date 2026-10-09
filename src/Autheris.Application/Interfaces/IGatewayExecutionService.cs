using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Interfaces;

public interface IGatewayExecutionService
{
    int LastDispatchedChildQueryCount { get; }

    Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first = null,
        int? after = null,
        CancellationToken ct = default);

    Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields = null,
        IReadOnlyDictionary<string, string[]>? requestHeaders = null,
        CancellationToken ct = default);

    /// <summary>
    /// 4a.3: one page of a table with optional ordering and the total row count under the same row filter
    /// (OData $orderby / $count). Ordering columns must be readable in clear text.
    /// </summary>
    /// <exception cref="Autheris.Domain.Exceptions.GatewayInvalidQueryException">An ordering column is unknown or not permitted.</exception>
    /// <exception cref="Autheris.Domain.Exceptions.GatewayNotImplementedException">The data source cannot order or count.</exception>
    Task<TableQueryPage> ExecuteTablePageAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        TablePageRequest request,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Paged table queries are not supported by this execution service.");

    Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default);
}

/// <summary>A column to order by (4a.3).</summary>
public sealed record TableOrderBy(string Column, bool Descending = false);

/// <summary>
/// Befund 2.1: Parameterized SQL filter clause pushed down into the database query with Zero-Trust guardrails.
/// </summary>
public sealed record TableFilterClause(
    string SqlPredicate,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> ReferencedColumns)
{
    /// <summary>Optional dialect-specific predicate builder when AST is available.</summary>
    public Func<DatabaseDialect, string>? DialectSqlFactory { get; init; }

    public string GetSqlPredicate(DatabaseDialect dialect) =>
        DialectSqlFactory?.Invoke(dialect) ?? SqlPredicate;
}

/// <summary>Page request of <see cref="IGatewayExecutionService.ExecuteTablePageAsync"/>.</summary>
public sealed record TablePageRequest(
    int First,
    int After,
    IReadOnlyList<string>? RequestedFields = null,
    IReadOnlyList<TableOrderBy>? OrderBy = null,
    TableFilterClause? Filter = null,
    bool IncludeTotalCount = false,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null);

/// <summary>Rows of the page, the access decision and (when requested) the total row count.</summary>
public sealed record TableQueryPage(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    TableAccessDecision Decision,
    long? TotalCount);

/// <summary>
/// Keys of <see cref="DataSourceExecutionContext.Items"/> that carry ordering, filtering and counting between the gateway and the
/// SQL executor (4a.3 / 2.1). An executor that does not know them leaves <see cref="TotalCount"/> unset.
/// </summary>
public static class TableQueryItems
{
    /// <summary>Request: <see cref="IReadOnlyList{T}"/> of <see cref="TableOrderBy"/>.</summary>
    public const string OrderBy = "TableQuery.OrderBy";

    /// <summary>Request: <see cref="TableFilterClause"/> pushed down to SQL WHERE clause.</summary>
    public const string Filter = "TableQuery.Filter";

    /// <summary>Request: <c>true</c> to count all rows under the same filters.</summary>
    public const string CountTotal = "TableQuery.CountTotal";

    /// <summary>Response: the total row count (<see cref="long"/>).</summary>
    public const string TotalCount = "TableQuery.TotalCount";
}

