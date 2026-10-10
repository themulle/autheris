namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public sealed record GovernedSqlQueryRequest(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null,
    string? DataSourceName = null,
    SqlRowLimit? RowLimit = null,
    string? Transport = null);

public sealed record GovernedSqlResult(
    string OriginalSql,
    string RewrittenSql,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    long ElapsedMilliseconds,
    bool Truncated = false,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<SqlResultColumn>? ColumnDescriptions = null);

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

    Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
