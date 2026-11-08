using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Policy epoch increment (own partial so the catalog partial stays under its size ceiling).</summary>
public partial class SqlServerGovernanceRepository
{
    private async Task<long> IncrementTableEpochInternalAsync(SqlConnection conn, SqlTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE POLICY_EPOCHS
                                SET epoch = epoch + 1, updated_at = @now
                                OUTPUT inserted.epoch
                                WHERE LOWER(domain) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            cmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));

            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar != null)
            {
                return Convert.ToInt64(scalar);
            }

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            // Missing epoch row: key the new row by the real TABLES.id (a random id would never match later updates).
            insertCmd.CommandText = @"MERGE POLICY_EPOCHS WITH (HOLDLOCK) AS target
                                      USING (SELECT id AS table_id, source_name AS domain, schema_name, table_name FROM TABLES
                                             WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)) AS source
                                      ON target.table_id = source.table_id
                                      WHEN MATCHED THEN UPDATE SET epoch = target.epoch + 1, updated_at = @now
                                      WHEN NOT MATCHED THEN INSERT (table_id, domain, schema_name, table_name, epoch, updated_at)
                                          VALUES (source.table_id, source.domain, source.schema_name, source.table_name, 2, @now)
                                      OUTPUT inserted.epoch;";
            insertCmd.AddBound("@domain", table.Domain);
            insertCmd.AddBound("@schema", table.Schema);
            insertCmd.AddBound("@table", table.TableName);
            insertCmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));

            var insScalar = await insertCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (insScalar != null)
            {
                return Convert.ToInt64(insScalar);
            }

            // Table no longer in the catalog: write no row and do not throw, so a revoke/withdraw in the same transaction still commits.
            _logger?.LogWarning("Policy epoch was not advanced: the table is not registered in the catalog.");
            return 0L;
        }
    }
}
