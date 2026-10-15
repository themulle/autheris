namespace Autheris.Infrastructure.Persistence;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public partial class SqliteGovernanceRepository : ICdcWatermarkRepository
{
    public async Task<long?> GetAsync(string source, string domain, string schema, string table, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT watermark FROM autheris_cdc_watermarks
                WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table;";
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);

            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    public async Task<bool> TryAdvanceAsync(string source, string domain, string schema, string table, long watermark, long fencingToken, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO autheris_cdc_watermarks (source, domain_name, schema_name, table_name, watermark, fencing_token, updated_at, updated_by)
                VALUES (@source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy)
                ON CONFLICT(source, domain_name, schema_name, table_name) DO UPDATE SET
                    watermark = MAX(watermark, excluded.watermark),
                    fencing_token = excluded.fencing_token,
                    updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by
                WHERE excluded.fencing_token >= autheris_cdc_watermarks.fencing_token;";
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@domain", domain);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            cmd.Parameters.AddWithValue("@watermark", watermark);
            cmd.Parameters.AddWithValue("@token", fencingToken);
            cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@updatedBy", Environment.MachineName);

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
    }
}
