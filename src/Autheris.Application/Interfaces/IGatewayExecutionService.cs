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

    Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableMetadata metadata,
        bool recordAudit = true,
        CancellationToken ct = default) =>
        CheckTableAccessAsync(principal, metadata.Identifier, ct);

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

    /// <summary>
    /// WP-ARC-B-01: dialect-neutral predicate builder. The argument maps a column name as written in the request to the
    /// catalog spelling; the factory returns Trino syntax (delimited identifiers, named client parameters <c>@name</c>
    /// whose values are in <see cref="Parameters"/>). The governed compile path translates that text per dialect.
    /// </summary>
    public Func<Func<string, string>, string>? TrinoSqlFactory { get; init; }

    /// <summary>
    /// The predicate in Trino syntax. Values never enter the text; they travel as <see cref="Parameters"/>.
    /// Fail-closed: a clause without a Trino builder cannot be planned (no best-effort text).
    /// </summary>
    public string GetTrinoPredicate(Func<string, string>? columnResolver = null)
    {
        if (TrinoSqlFactory is not { } factory)
        {
            throw new NotSupportedException("This filter clause has no Trino predicate builder and cannot be pushed down through the governed pipeline.");
        }

        return factory(columnResolver ?? (static column => column));
    }

    /// <summary>A clause whose predicate is already Trino text (identifiers quoted by the caller, literals inlined by a safe translator).</summary>
    public static TableFilterClause FromTrinoText(string trinoPredicate, IReadOnlyList<string> referencedColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trinoPredicate);
        ArgumentNullException.ThrowIfNull(referencedColumns);
        return new TableFilterClause(trinoPredicate, new Dictionary<string, object?>(), referencedColumns)
        {
            TrinoSqlFactory = _ => trinoPredicate
        };
    }
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

