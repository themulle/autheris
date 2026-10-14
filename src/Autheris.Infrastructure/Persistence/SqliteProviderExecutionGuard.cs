using System.Data.Common;
using Autheris.Application.Sql.Interfaces;
using Microsoft.Data.Sqlite;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// SEC-X1-10 (X1.2): the <see cref="IProviderExecutionGuard"/> of the gateway. A SQLite connection runs its work under
/// <see cref="SqliteExecutionGuard"/> (an interrupt at the timeout and on cancellation); every other provider enforces the command
/// timeout itself and passes through.
/// </summary>
public sealed class SqliteProviderExecutionGuard : IProviderExecutionGuard
{
    public Task<T> RunAsync<T>(DbConnection connection, TimeSpan timeout, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(work);
        return connection is SqliteConnection
            ? SqliteExecutionGuard.RunAsync(connection, timeout, work, ct)
            : work();
    }
}
