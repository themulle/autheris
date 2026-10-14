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
/// allowed for transports listed in WebSql.CrossSource.AllowedTransports (default: WebSql, Trino); every other
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
}

public sealed record GovernedSqlQueryRequest(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? DataSourceName = null,
    SqlRowLimit? RowLimit = null,
    string? Transport = null);

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

    /// <summary>G-6: validates a statement through the governed pipeline without executing it and without returning SQL text.</summary>
    Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
