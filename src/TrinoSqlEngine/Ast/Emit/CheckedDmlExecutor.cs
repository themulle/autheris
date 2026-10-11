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
}

/// <summary>The default <see cref="ICheckedDmlExecutor"/> over an ADO.NET connection.</summary>
public sealed class CheckedDmlExecutor : ICheckedDmlExecutor
{
    private readonly DbCommandCompiledSqlBinder _binder;

    /// <param name="binder">The provider binder of the dialect; only the engine's own binders can bind a checked statement.</param>
    public CheckedDmlExecutor(DbCommandCompiledSqlBinder binder)
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
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            _binder.BindForCheckedExecution(command, compiled, clientParameterValues);
            int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // an unreported count (-1, for example SET NOCOUNT ON) differs from the expected count and fails closed
            DmlCheckOption.Enforce(compiled, affected);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
