namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// CR-ADG-43: executes a governed DML statement with the row-count contract of the INSERT check option (CR-ADG-35). The binders
/// refuse a <see cref="CompiledSql.RequiresRowCountCheck"/> statement, so this is the only path that runs one.
/// </summary>
public interface ICheckedDmlExecutor
{
    /// <summary>
    /// Runs <paramref name="compiled"/> in a transaction on <paramref name="connection"/> (opened when closed; the caller owns and
    /// disposes it), compares the affected row count with <see cref="CompiledSql.ExpectedAffectedRows"/> before the commit and rolls
    /// back on any difference (<see cref="DmlCheckOptionViolationException"/>) or error. Returns the affected row count.
    /// </summary>
    Task<int> ExecuteAsync(
        DbConnection connection,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// X0k (G-3): runs <paramref name="compiled"/> inside the caller's session transaction (no nested transaction: PostgreSQL pins the
    /// tenant with a transaction-local setting) with the command timeout of <paramref name="limits"/>. Binds, executes, enforces the
    /// check option and then <see cref="CheckedDmlLimits.MaxAffectedRows"/>. This method is the single owner of the completion of
    /// <paramref name="transaction"/> (SEC-X1-06): it commits on success and rolls back on any failure, including a timeout and a
    /// cancellation (the rollback ignores the token). The caller must neither commit nor roll it back again, and disposes it.
    /// A transaction that is no longer usable (completed, or on a closed connection) is rejected.
    /// </summary>
    Task<int> ExecuteInTransactionAsync(
        DbTransaction transaction,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        CheckedDmlLimits limits,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Limits of one governed DML execution (X0k). <paramref name="CommandTimeoutSeconds"/> is applied to the command (0 = provider
/// default). <paramref name="MaxAffectedRows"/>: 0 = no limit; with a limit, an unreported count (-1) fails closed.
/// </summary>
public sealed record CheckedDmlLimits(int CommandTimeoutSeconds, long MaxAffectedRows);

/// <summary>The default <see cref="ICheckedDmlExecutor"/> over an ADO.NET connection.</summary>
public sealed class CheckedDmlExecutor : ICheckedDmlExecutor
{
    private readonly DbCommandCompiledSqlBinder _binder;

    /// <param name="binder">The provider binder of the dialect; only the engine's own binders can bind a checked statement.</param>
    /// <remarks>Internal (SEC-X1-07): <see cref="CompiledSqlBinders.CheckedExecutorFor"/> is the only factory.</remarks>
    internal CheckedDmlExecutor(DbCommandCompiledSqlBinder binder)
    {
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
    }

    public async Task<int> ExecuteAsync(
        DbConnection connection,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(clientParameterValues);
        if (!_binder.CanBind(compiled.Dialect))
        {
            throw new InvalidOperationException("The compiled SQL was generated for another dialect.");
        }

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return await RunAndCompleteAsync(transaction, compiled, clientParameterValues, new CheckedDmlLimits(0, 0), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteInTransactionAsync(
        DbTransaction transaction,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        CheckedDmlLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(clientParameterValues);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.CommandTimeoutSeconds < 0) throw new ArgumentOutOfRangeException(nameof(limits), "The command timeout must not be negative.");
        if (limits.MaxAffectedRows < 0) throw new ArgumentOutOfRangeException(nameof(limits), "The affected-row limit must not be negative.");

        // Nothing is run on a transaction that is not usable. It is not completed here either: a completed transaction has nothing to complete.
        if (transaction.Connection is not { State: ConnectionState.Open })
        {
            throw new InvalidOperationException("The transaction is completed or its connection is not open.");
        }

        // From here on the executor owns the completion, so a rejected statement (for example of another dialect) rolls back as well.
        return await RunAndCompleteAsync(transaction, compiled, clientParameterValues, limits, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunAndCompleteAsync(
        DbTransaction transaction,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        CheckedDmlLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_binder.CanBind(compiled.Dialect))
            {
                throw new InvalidOperationException("The compiled SQL was generated for another dialect.");
            }

            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            if (limits.CommandTimeoutSeconds > 0) command.CommandTimeout = limits.CommandTimeoutSeconds;
            _binder.BindForCheckedExecution(command, compiled, clientParameterValues);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // an unreported count (-1, for example SET NOCOUNT ON) differs from the expected count and fails closed
            if (CompilerHooks.IsDisabled(SecurityControl.CheckedDmlCommitBeforeEnforce))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!CompilerHooks.IsDisabled(SecurityControl.CheckedDmlRowCount))
            {
                DmlCheckOption.Enforce(compiled, affected);
            }

            if (CompilerHooks.IsDisabled(SecurityControl.CheckedDmlCommitBeforeEnforce))
            {
                return affected;
            }

            bool enforceLimit = !CompilerHooks.IsDisabled(SecurityControl.CheckedDmlMaxAffectedRows);
            if (enforceLimit && limits.MaxAffectedRows > 0 && (affected < 0 || affected > limits.MaxAffectedRows))
            {
                throw new DmlAffectedRowsLimitException(compiled.Dialect);
            }

            // L2: every check has passed. The commit does not take the caller's token: a client that disconnects at this moment must not
            // leave the outcome to a cancellation race between the commit and the rollback of the catch block.
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return affected;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RollbackQuietlyAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // a failed rollback (for example a dead connection) must not mask the original error; the server discards the transaction
        }
    }
}

/// <summary>
/// The executor of Databricks (X0k, S-2). Delta has no transaction, so a governed DML statement cannot be rolled back and
/// <see cref="CheckedDmlLimits.MaxAffectedRows"/> cannot be enforced atomically: DML is rejected, before any command is created or
/// request is sent, while a limit is configured. A row-count check is never supported (the compiler does not emit one for Delta).
/// </summary>
internal sealed class DatabricksCheckedDmlExecutor : ICheckedDmlExecutor
{
    private readonly DbCommandCompiledSqlBinder _binder;

    internal DatabricksCheckedDmlExecutor(DbCommandCompiledSqlBinder binder)
    {
        _binder = binder ?? throw new ArgumentNullException(nameof(binder));
    }

    public Task<int> ExecuteAsync(
        DbConnection connection,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Databricks has no transaction; use ExecuteInTransactionAsync.");

    public async Task<int> ExecuteInTransactionAsync(
        DbTransaction transaction,
        CompiledSql compiled,
        IReadOnlyDictionary<string, object?> clientParameterValues,
        CheckedDmlLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(clientParameterValues);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.CommandTimeoutSeconds < 0) throw new ArgumentOutOfRangeException(nameof(limits), "The command timeout must not be negative.");
        if (limits.MaxAffectedRows < 0) throw new ArgumentOutOfRangeException(nameof(limits), "The affected-row limit must not be negative.");
        if (!_binder.CanBind(compiled.Dialect))
        {
            throw new InvalidOperationException("The compiled SQL was generated for another dialect.");
        }

        // fail closed before anything reaches the facade connection
        if (limits.MaxAffectedRows > 0)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Databricks DML while an affected-row limit is configured");
        }

        if (compiled.RequiresRowCountCheck)
        {
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, "Databricks has no row-count check");
        }

        // there is no transaction to complete: the facade transaction stays the caller's
        var connection = transaction.Connection ?? throw new InvalidOperationException("The transaction is completed.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (limits.CommandTimeoutSeconds > 0) command.CommandTimeout = limits.CommandTimeoutSeconds;
        _binder.Bind(command, compiled, clientParameterValues);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
