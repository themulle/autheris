namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;

/// <summary>
/// Status and result representation of a governed WebSQL statement execution, compatible with the Trino REST client protocol.
/// </summary>
public sealed record StatementExecutionStatus(
    string StatementId,
    string State, // "QUEUED", "RUNNING", "FINISHED", "FAILED", "CANCELED"
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<object?>>? Data,
    long ElapsedTimeMillis,
    SqlFailure? Failure = null,
    bool Truncated = false,
    IReadOnlyList<TrinoColumnType>? ColumnTypes = null)
{
    public string? ErrorMessage => Failure?.FixedMessage;
    public string? ErrorCode => Failure?.Code;
    public int? ErrorLine => Failure?.Line;
    public int? ErrorColumn => Failure?.Column;
}

/// <summary>
/// Manages asynchronous and synchronous WebSQL statement executions, supporting the Trino HTTP client protocol
/// with wait_timeout polling, statement continuation via statement IDs, and cancellation.
/// </summary>
public interface IWebSqlStatementManager
{
    /// <summary>
    /// Submits a query for governed execution. If waitTimeout is greater than zero and execution completes
    /// within that duration, returns the final result synchronously (State = "FINISHED"). Otherwise returns
    /// State = "RUNNING" with a continuation NextUri.
    /// </summary>
    Task<StatementExecutionStatus> SubmitOrWaitAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        TimeSpan waitTimeout,
        DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
        CancellationToken ct = default);

    /// <summary>
    /// Resumes or polls an existing statement execution. Waits up to waitTimeout for completion.
    /// Fails closed if the caller does not match the statement's tenant and user.
    /// </summary>
    Task<StatementExecutionStatus> GetStatusOrWaitAsync(
        string statementId,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        TimeSpan waitTimeout,
        DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a running statement. Fails closed if the caller does not match the statement's tenant and user.
    /// </summary>
    Task<bool> CancelStatementAsync(
        string statementId,
        ClaimsPrincipal user,
        Sid owner,
        TenantId tenantId,
        CancellationToken ct = default);
}
