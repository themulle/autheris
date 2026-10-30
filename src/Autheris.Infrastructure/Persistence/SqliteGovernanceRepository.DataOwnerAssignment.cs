using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository : IDataOwnerAssignmentRepository
{
    /// <summary>Dialect SQL of the data owner assignment (issue #393 part b); the shared statements live in <see cref="DataOwnerAssignmentSqlCore"/>.</summary>
    internal static class DataOwnerAssignmentSql
    {
        public const string SelectTableId = @"SELECT id FROM TABLES
                                WHERE source_name = @domain COLLATE NOCASE AND schema_name = @schema COLLATE NOCASE AND table_name = @table COLLATE NOCASE";
    }

    public async Task<DataOwnerAssignmentResult> ApplyTableOwnerAssignmentsAsync(DataOwnerAssignmentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            var result = await DataOwnerAssignmentSqlCore.RunAsync(conn, tx, DataOwnerAssignmentSql.SelectTableId, request, ct).ConfigureAwait(false);
            if (!result.Success || request.DryRun)
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                OnTransactionRolledBack(tx);
                return result;
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
            return result;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }
}
