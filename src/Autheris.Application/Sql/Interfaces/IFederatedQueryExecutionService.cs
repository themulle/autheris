namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public interface IFederatedQueryExecutionService
{
    Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct = default) =>
        ExecuteGovernedQueryAsync(request, user, tenantId, (reader, token) => rowWriter(reader, new GovernedResultInfo(SqlResultColumns.Describe(reader), ImmutableArray<bool>.Empty), token), ct);

    Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default) =>
        ExecuteGovernedQueryAsync(request, user, tenantId, (reader, _, token) => rowWriter(reader, token), ct);

    Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);

    /// <summary>G-6: plans a cross-source statement without executing it and without returning SQL text.</summary>
    Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
