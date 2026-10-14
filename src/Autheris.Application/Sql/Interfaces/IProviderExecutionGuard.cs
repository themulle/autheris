namespace Autheris.Application.Sql.Interfaces;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SEC-X1-10 (X1.2): provider-specific protection around the execution of a governed statement. SQLite has no server-side statement
/// timeout (<c>CommandTimeout</c> is only a busy timeout), so a recursive CTE would run unbounded and hold a worker thread; its guard
/// interrupts the statement at the timeout and on cancellation. The executor runs the execution of every governed statement (the read,
/// including the streaming of its rows, and the DML) through this guard. The default passes through.
/// </summary>
public interface IProviderExecutionGuard
{
    /// <exception cref="TimeoutException">The statement exceeded <paramref name="timeout"/> and was interrupted.</exception>
    Task<T> RunAsync<T>(DbConnection connection, TimeSpan timeout, Func<Task<T>> work, CancellationToken ct);
}

/// <summary>The guard of providers with their own server-side timeout (PostgreSQL, SQL Server, Oracle: <c>CommandTimeout</c>).</summary>
public sealed class PassThroughExecutionGuard : IProviderExecutionGuard
{
    public static PassThroughExecutionGuard Instance { get; } = new();

    public Task<T> RunAsync<T>(DbConnection connection, TimeSpan timeout, Func<Task<T>> work, CancellationToken ct) => work();
}
