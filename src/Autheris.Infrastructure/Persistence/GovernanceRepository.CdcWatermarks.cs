namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : ICdcWatermarkRepository
{
    private const string SelectWatermarkPg = "SELECT watermark FROM autheris_cdc_watermarks WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table;";
    private const string SelectWatermarkSs = "SELECT watermark FROM dbo.autheris_cdc_watermarks WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table;";

    private const string TryAdvancePg = @"
            INSERT INTO autheris_cdc_watermarks AS t (source, domain_name, schema_name, table_name, watermark, fencing_token, updated_at, updated_by)
            VALUES (@source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy)
            ON CONFLICT (source, domain_name, schema_name, table_name) DO UPDATE SET
                watermark = GREATEST(t.watermark, EXCLUDED.watermark),
                fencing_token = EXCLUDED.fencing_token,
                updated_at = EXCLUDED.updated_at,
                updated_by = EXCLUDED.updated_by
            WHERE EXCLUDED.fencing_token >= t.fencing_token;";

    private const string TryAdvanceSs = @"
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

    public async Task<long?> GetAsync(string source, string domain, string schema, string table, CancellationToken ct = default)
    {
        var sql = _dialect is PostgreSqlGovernanceDialect ? SelectWatermarkPg : SelectWatermarkSs;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@source", source);
        cmd.AddBound("@domain", domain);
        cmd.AddBound("@schema", schema);
        cmd.AddBound("@table", table);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryAdvanceAsync(string source, string domain, string schema, string table, long watermark, long fencingToken, CancellationToken ct = default)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = isPg ? TryAdvancePg : TryAdvanceSs;
        cmd.AddBound("@source", source);
        cmd.AddBound("@domain", domain);
        cmd.AddBound("@schema", schema);
        cmd.AddBound("@table", table);
        cmd.AddBound("@watermark", watermark);
        cmd.AddBound("@token", fencingToken);
        cmd.AddBound("@updatedAt", TimeProvider.System.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        cmd.AddBound("@updatedBy", Environment.MachineName);

        if (isPg)
        {
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }

        var affected = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(affected, CultureInfo.InvariantCulture) > 0;
    }
}
