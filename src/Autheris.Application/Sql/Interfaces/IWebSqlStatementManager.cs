namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Status and result representation of a governed WebSQL statement execution, compatible with the Trino REST client protocol.
/// </summary>
public sealed record StatementExecutionStatus(
    string StatementId,
    string State, // "QUEUED", "RUNNING", "FINISHED", "FAILED", "CANCELED"
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<object?>>? Data,
    string? NextUri,
    string? ErrorMessage,
    long ElapsedTimeMillis);

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
        TenantId tenantId,
        TimeSpan waitTimeout,
        CancellationToken ct = default);

    /// <summary>
    /// Resumes or polls an existing statement execution. Waits up to waitTimeout for completion.
    /// Fails closed if the caller does not match the statement's tenant and user.
    /// </summary>
    Task<StatementExecutionStatus> GetStatusOrWaitAsync(
        string statementId,
        ClaimsPrincipal user,
        TenantId tenantId,
        TimeSpan waitTimeout,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a running statement. Fails closed if the caller does not match the statement's tenant and user.
    /// </summary>
    Task<bool> CancelStatementAsync(
        string statementId,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}
