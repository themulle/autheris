using System.Data.Common;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Rolls a failed transaction back without ever hiding the original failure. The rollback ignores the request cancellation (a cancelled token must not skip it and
/// must not prevent <c>OnTransactionRolledBack</c> from running), and a transaction that already completed (a commit hook failed after the commit) is left alone.
/// </summary>
internal static class TransactionRollback
{
    internal static async Task RollbackAsync(DbTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        try
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or ObjectDisposedException)
        {
            // already completed or the connection is gone: the server rolls the transaction back; the caller rethrows the original exception
        }
    }
}
