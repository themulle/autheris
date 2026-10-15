using System.Text;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// WP-PF-04: builds the multi-row <c>INSERT</c> of audit rows shared by the three governance repositories. Parameter names are
/// <c>@name0..@nameN</c> per row; the statement size is bounded (13 parameters per row, well below the 2,100-parameter limit of SQL Server).
/// The chain hashes are computed in memory before the rows are written, so the statement shape never influences the chain.
/// </summary>
internal static class AuditBatchInsertSql
{
    internal const int MaxRowsPerStatement = 100;

    private static readonly string[] ParameterNames =
        ["id", "occ", "event", "actor", "target", "col", "dec", "trace", "det", "prev", "hash", "tenantId", "seq"];

    internal static string Build(int rows, string table = "AUDIT_LOG_ENTRIES")
    {
        if (rows < 1 || rows > MaxRowsPerStatement)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, $"Between 1 and {MaxRowsPerStatement} rows per statement.");
        }

        var sb = new StringBuilder(128 + rows * 96);
        sb.Append("INSERT INTO ").Append(table).Append(" (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq) VALUES ");
        for (int r = 0; r < rows; r++)
        {
            if (r > 0)
            {
                sb.Append(", ");
            }

            sb.Append('(');
            for (int c = 0; c < ParameterNames.Length; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }

                sb.Append('@').Append(ParameterNames[c]).Append(r);
            }

            sb.Append(')');
        }

        return sb.ToString();
    }
}

/// <summary>One audit row after its chain hash was computed (values exactly as written to the database).</summary>
internal readonly record struct AuditInsertRow(
    string Id,
    string OccurredAt,
    string? EventType,
    string? ActorSid,
    string? TargetTable,
    string? TargetColumn,
    string? Decision,
    string? TraceId,
    string? DetailsJson,
    string PrevHash,
    string EntryHash,
    string TenantId,
    long Seq);
