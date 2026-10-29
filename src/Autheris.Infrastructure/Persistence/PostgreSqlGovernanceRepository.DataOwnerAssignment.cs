using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository : IDataOwnerAssignmentRepository
{
    /// <summary>Dialect SQL of the data owner assignment (issue #393 part b); the shared statements live in <see cref="DataOwnerAssignmentSqlCore"/>.</summary>
    internal static class DataOwnerAssignmentSql
    {
        public const string SelectTableId = @"SELECT id FROM TABLES
                                WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
    }

    public async Task<DataOwnerAssignmentResult> ApplyTableOwnerAssignmentsAsync(DataOwnerAssignmentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
