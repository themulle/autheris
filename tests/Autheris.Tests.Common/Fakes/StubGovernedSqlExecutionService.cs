using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Configurable <see cref="IGovernedSqlExecutionService"/> test double for unit tests.
/// </summary>
public sealed class StubGovernedSqlExecutionService : IGovernedSqlExecutionService
{
    public static readonly StubGovernedSqlExecutionService Empty = new();

    private readonly List<GovernedSqlQueryRequest> _requests = [];

    public IReadOnlyList<GovernedSqlQueryRequest> Requests => _requests;

    public Func<GovernedSqlQueryRequest, ClaimsPrincipal, TenantId, Func<DbDataReader, CancellationToken, Task>, CancellationToken, Task>? OnStream { get; init; }
    public Func<GovernedSqlQueryRequest, ClaimsPrincipal, TenantId, CancellationToken, Task<GovernedSqlResult>>? OnBuffered { get; init; }
    public Func<string, ClaimsPrincipal, TenantId, CancellationToken, Task>? OnValidate { get; init; }

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
        => ExecuteGovernedQueryAsync(
            request,
            user,
            tenantId,
            (reader, token) => rowWriter(reader, new GovernedResultInfo(SqlResultColumns.Describe(reader), ImmutableArray<bool>.Empty), token),
            ct);

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        _requests.Add(request);
        return OnStream is not null
            ? OnStream(request, user, tenantId, rowWriter, ct)
            : Task.CompletedTask;
    }

    public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        _requests.Add(request);
        return OnBuffered is not null
            ? OnBuffered(request, user, tenantId, ct)
            : Task.FromResult(new GovernedSqlResult(request.Sql, [], [], 0, 0));
    }

    public Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
        => OnValidate is not null
            ? OnValidate(rawSql, user, tenantId, ct)
            : Task.CompletedTask;
}
