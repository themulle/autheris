namespace Autheris.Infrastructure.Persistence;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public partial class SqlServerGovernanceRepository : ICdcWatermarkRepository
{
    public async Task<long?> GetAsync(string source, string domain, string schema, string table, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT watermark FROM dbo.autheris_cdc_watermarks
            WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table;";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@domain", domain);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryAdvanceAsync(string source, string domain, string schema, string table, long watermark, long fencingToken, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // One batch, one transaction: the UPDLOCK/HOLDLOCK range lock makes update-or-insert race free and the fencing
        // predicate keeps a stale writer from touching the row. The result is the number of affected rows (0 = fenced out).
        cmd.CommandText = @"
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @affected INT = 0;
            UPDATE dbo.autheris_cdc_watermarks WITH (UPDLOCK, HOLDLOCK)
               SET watermark = IIF(watermark > @watermark, watermark, @watermark),
                   fencing_token = @token,
                   updated_at = @updatedAt,
                   updated_by = @updatedBy
             WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table
               AND fencing_token <= @token;
            SET @affected = @@ROWCOUNT;
            IF @affected = 0 AND NOT EXISTS (
                SELECT 1 FROM dbo.autheris_cdc_watermarks WITH (UPDLOCK, HOLDLOCK)
                 WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table)
            BEGIN
                INSERT INTO dbo.autheris_cdc_watermarks (source, domain_name, schema_name, table_name, watermark, fencing_token, updated_at, updated_by)
                VALUES (@source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy);
                SET @affected = @@ROWCOUNT;
            END
            COMMIT;
            SELECT @affected;";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@domain", domain);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@watermark", watermark);
        cmd.Parameters.AddWithValue("@token", fencingToken);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@updatedBy", Environment.MachineName);

        var affected = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(affected, CultureInfo.InvariantCulture) > 0;
    }
}
