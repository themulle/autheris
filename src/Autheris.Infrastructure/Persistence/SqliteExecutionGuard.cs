using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// WP-X0h (SEC-X1-10): the interrupt-based execution timeout of governed SQLite statements. <see cref="SqliteCommand.CommandTimeout"/>
/// is only a busy timeout, so a recursive CTE would run unbounded and hold a worker thread. The guard installs a progress handler on
/// the connection (called from inside the VM every 1000 instructions, on the thread that runs the statement); once the timeout
/// elapsed or the request is cancelled, the handler aborts the statement with <c>SQLITE_INTERRUPT</c>. A progress handler is used
/// instead of <c>sqlite3_interrupt</c> from a timer thread because it can never touch a connection that was already returned or
/// disposed. The timeout surfaces as <see cref="TimeoutException"/> (mapped to <c>SQL_TIMEOUT</c>), a cancellation as
/// <see cref="OperationCanceledException"/>. A statement inside a transaction is rolled back by the caller (the checked DML executor
/// rolls back on any failure).
/// </summary>
public static class SqliteExecutionGuard
{
    private const int InstructionsPerCheck = 1000;
    private const int SqliteInterrupt = 9;   // SQLITE_INTERRUPT

    private sealed class State
    {
        public int Reason;   // 0 = running, 1 = timeout, 2 = cancelled
    }

    /// <summary>
    /// Runs <paramref name="work"/> (every command it issues on <paramref name="connection"/>) under the interrupt guard.
    /// The handler is removed again when the work ends, so the connection is unlimited outside the guard.
    /// </summary>
    /// <exception cref="NotSupportedException">The connection is not a SQLite connection (no interrupt exists).</exception>
    public static async Task<T> RunAsync<T>(DbConnection connection, TimeSpan timeout, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(work);
        if (connection is not SqliteConnection sqlite)
        {
            throw new NotSupportedException("The execution guard interrupts SQLite statements only.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();
        var handle = sqlite.Handle ?? throw new InvalidOperationException("The SQLite connection is not open.");

        var state = new State();
        long start = Stopwatch.GetTimestamp();
        raw.sqlite3_progress_handler(handle, InstructionsPerCheck, _ =>
        {
            if (ct.IsCancellationRequested)
            {
                Volatile.Write(ref state.Reason, 2);
                return 1;
            }

            if (Stopwatch.GetElapsedTime(start) >= timeout)
            {
                Volatile.Write(ref state.Reason, 1);
                return 1;
            }

            return 0;
        }, null);

        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteInterrupt && Volatile.Read(ref state.Reason) != 0)
        {
            // Fixed text: no statement, value or table name (INV-16).
            throw Volatile.Read(ref state.Reason) == 1
                ? new TimeoutException("The SQLite statement exceeded the execution timeout and was interrupted.", ex)
                : new OperationCanceledException("The SQLite statement was interrupted by the cancellation of the request.", ex, ct);
        }
        finally
        {
            raw.sqlite3_progress_handler(handle, 0, null, null);
        }
    }
}
