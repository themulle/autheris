namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Transport names carried in <see cref="GovernedSqlQueryRequest.Transport"/>. Cross-source (federated) execution is only
/// allowed for transports listed in WebSql.CrossSource.AllowedTransports (default: WebSql, Trino; never Olap); every other
/// transport must be opted in explicitly.
/// </summary>
public static class SqlTransports
{
    public const string WebSql = "WebSql";
    public const string Trino = "Trino";
    public const string Rest = "Rest";
    public const string Mcp = "Mcp";
    public const string ArrowExport = "ArrowExport";
    public const string FlightSql = "FlightSql";
    public const string SqlEndpoints = "SqlEndpoints";
    public const string AsyncJob = "AsyncJob";

    /// <summary>The in-memory DuckDB OLAP endpoint (<c>POST /api/v1/olap/query</c>). Never part of the default allowed list.</summary>
    public const string Olap = "Olap";

    /// <summary>Every registered transport name; <c>WebSql:CrossSource:AllowedTransports</c> entries are validated against it at start-up.</summary>
    public static IReadOnlyList<string> All { get; } =
        [WebSql, Trino, Rest, Mcp, ArrowExport, FlightSql, SqlEndpoints, AsyncJob, Olap];
}

/// <param name="PlannedTableRead">
/// True only for a table read whose SQL the gateway planned itself (OData, GraphQL, MCP, connectors). Such a read is not
/// client SQL and is therefore not gated by <c>WebSql:Enabled</c>; it still runs through the full governed pipeline.
/// Never set from client input.
/// </param>
public sealed record GovernedSqlQueryRequest(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? DataSourceName = null,
    SqlRowLimit? RowLimit = null,
    string? Transport = null,
    bool PlannedTableRead = false);

public sealed record GovernedSqlResult(
    string OriginalSql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    long ElapsedMilliseconds,
    bool Truncated = false,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<SqlResultColumn>? ColumnDescriptions = null,
    [property: System.Text.Json.Serialization.JsonIgnore] bool InDbColumnMaskingExecuted = false);

public interface IGovernedSqlExecutionService
{
    Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default);

    Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// WP-ARC-C-13: Streams governed query rows asynchronously via <c>yield return</c> without buffering the entire result set in memory.
    /// Governance evaluation, AST compilation, and audit recording complete before rows are yielded.
    /// </summary>
    async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffered = await ExecuteQueryBufferedAsync(request, user, tenantId, ct).ConfigureAwait(false);
        foreach (var row in buffered.Rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    /// <summary>G-6: validates a statement through the governed pipeline without executing it and without returning SQL text.</summary>
    Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
