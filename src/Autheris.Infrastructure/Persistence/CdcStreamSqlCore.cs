namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Streaming;

internal enum CdcSqlDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

/// <summary>
/// Engine-neutral SQL of the governed CDC stream store and key store (PLAN-CDC-99 D-A4). The three governance repositories hand a
/// connection (and own connection lifetime, gating and pooling); this class owns the statements and the append transaction. Only the
/// few statements that genuinely differ per engine (insert-if-absent, the fenced watermark upsert, row limits) are dialect branches.
/// All values are parameters; identifiers are constants.
/// </summary>
internal sealed class CdcStreamSqlCore(CdcSqlDialect dialect)
{
    private const int EventInsertChunk = 50;
    private const int ImageInsertChunk = 50;
    private const int InListChunk = 200;
    private const int MaxReadBatch = 10_000;

    private readonly string _streams = Table(dialect, "autheris_cdc_streams");
    private readonly string _events = Table(dialect, "autheris_cdc_stream_events");
    private readonly string _images = Table(dialect, "autheris_cdc_row_images");
    private readonly string _keys = Table(dialect, "autheris_cdc_stream_keys");
    private readonly string _watermarks = Table(dialect, "autheris_cdc_watermarks");

    public static bool IsUniqueViolation(Exception ex) => ex switch
    {
        Microsoft.Data.Sqlite.SqliteException s => s.SqliteErrorCode == 19 && s.SqliteExtendedErrorCode is 1555 or 2067,
        Npgsql.PostgresException p => p.SqlState == "23505",
        Microsoft.Data.SqlClient.SqlException q => q.Number is 2627 or 2601,
        _ => false
    };

    private static string Table(CdcSqlDialect d, string name) => d == CdcSqlDialect.SqlServer ? "dbo." + name : name;

    private static long Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void P(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static long L(DbDataReader r, int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static string? S(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static byte[] B(DbDataReader r, int i) => r.GetFieldValue<byte[]>(i);

    private static DbCommand Cmd(DbConnection conn, DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static string InList(DbCommand cmd, string prefix, IReadOnlyList<object> values)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('@').Append(prefix).Append(i.ToString(CultureInfo.InvariantCulture));
            P(cmd, prefix + i.ToString(CultureInfo.InvariantCulture), values[i]);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- streams

    private string DescriptorSelect =>
        $@"SELECT s.stream_id, s.source, s.domain_name, s.schema_name, s.table_name, s.status, s.activation_code,
                  s.head_offset, s.tail_offset, w.watermark, s.last_polled_ms
             FROM {_streams} s
             LEFT JOIN {_watermarks} w ON w.source = s.source AND w.domain_name = s.domain_name
                  AND w.schema_name = s.schema_name AND w.table_name = s.table_name";

    private static CdcStreamDescriptor ReadDescriptor(DbDataReader r) =>
        new(new CdcStreamId(r.GetString(0)), r.GetString(1), new TableIdentifier(r.GetString(2), r.GetString(3), r.GetString(4)),
            (CdcStreamStatus)(int)L(r, 5), S(r, 6), L(r, 7), L(r, 8), r.IsDBNull(9) ? null : L(r, 9),
            r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(L(r, 10)));

    private async Task<CdcStreamDescriptor?> FindByTableAsync(DbConnection conn, string source, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, DescriptorSelect + @"
             WHERE s.source = @source AND lower(s.domain_name) = lower(@d) AND lower(s.schema_name) = lower(@s) AND lower(s.table_name) = lower(@t)");
        P(cmd, "@source", source);
        P(cmd, "@d", table.Domain);
        P(cmd, "@s", table.Schema);
        P(cmd, "@t", table.TableName);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadDescriptor(r) : null;
    }

    public async Task<CdcStreamDescriptor> EnsureStreamAsync(DbConnection conn, string source, TableIdentifier table, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (await FindByTableAsync(conn, source, table, ct).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var now = Now();
        var values = @"@id, @source, @d, @s, @t, @status, 0, 1, 0, 0, @now, @now";
        var columns = "stream_id, source, domain_name, schema_name, table_name, status, head_offset, tail_offset, producer_fencing_token, retention_fencing_token, created_ms, updated_ms";
        var exists = @"NOT EXISTS (SELECT 1 FROM {STREAMS}{HINT} WHERE source = @source AND lower(domain_name) = lower(@d) AND lower(schema_name) = lower(@s) AND lower(table_name) = lower(@t))"
            .Replace("{STREAMS}", _streams, StringComparison.Ordinal)
            .Replace("{HINT}", dialect == CdcSqlDialect.SqlServer ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty, StringComparison.Ordinal);
        string sql = dialect switch
        {
            CdcSqlDialect.SqlServer => $@"SET XACT_ABORT ON; BEGIN TRAN;
                INSERT INTO {_streams} ({columns}) SELECT {values} WHERE {exists};
                COMMIT;",
            _ => $@"INSERT INTO {_streams} ({columns}) SELECT {values} WHERE {exists} ON CONFLICT DO NOTHING;"
        };

        await using (var insert = Cmd(conn, null, sql))
        {
            P(insert, "@id", CdcStreamId.New().Value);
            P(insert, "@source", source);
            P(insert, "@d", table.Domain);
            P(insert, "@s", table.Schema);
            P(insert, "@t", table.TableName);
            P(insert, "@status", (int)CdcStreamStatus.Active);
            P(insert, "@now", now);
            try
            {
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // A concurrent creator won the race: fall through to the re-read.
            }
        }

        return await FindByTableAsync(conn, source, table, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CDC stream could not be created.");
    }

    public async Task<CdcStreamDescriptor?> GetStreamAsync(DbConnection conn, CdcStreamId id, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, DescriptorSelect + " WHERE s.stream_id = @id");
        P(cmd, "@id", id.Value ?? string.Empty);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadDescriptor(r) : null;
    }

    public async Task<IReadOnlyList<CdcStreamDescriptor>> ListStreamsAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, DescriptorSelect + " ORDER BY s.created_ms, s.stream_id");
        var result = new List<CdcStreamDescriptor>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadDescriptor(r));
        }

        return result;
    }

    public async Task SetStatusAsync(DbConnection conn, CdcStreamId id, CdcStreamStatus status, string? activationCode, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $"UPDATE {_streams} SET status = @status, activation_code = @code, updated_ms = @now WHERE stream_id = @id");
        P(cmd, "@status", (int)status);
        P(cmd, "@code", activationCode);
        P(cmd, "@now", Now());
        P(cmd, "@id", id.Value ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task TouchPolledAsync(DbConnection conn, CdcStreamId id, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $"UPDATE {_streams} SET last_polled_ms = @at WHERE stream_id = @id");
        P(cmd, "@at", Ms(at));
        P(cmd, "@id", id.Value ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- append

    public async Task AppendAsync(DbConnection conn, CdcAppendBatch batch, CancellationToken ct)
    {
        CdcAppendBatchValidation.Validate(batch);
        var id = batch.StreamId.Value;
        var newHead = batch.ExpectedHeadOffset + batch.Events.Count;
        var resource = "cdc-stream:" + id;

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // (a) the fenced watermark upsert comes first: it serialises concurrent producers and rejects a stale token before anything else.
        if (!await UpsertWatermarkAsync(conn, tx, batch, ct).ConfigureAwait(false))
        {
            throw new FencingTokenRejectedException(resource, batch.FencingToken);
        }

        // (b) head compare-and-set fenced by the stream's producer token.
        await using (var cas = Cmd(conn, tx, $@"UPDATE {_streams}
                SET head_offset = @newHead, producer_fencing_token = @token, updated_ms = @now
              WHERE stream_id = @id AND head_offset = @expected AND producer_fencing_token <= @token"))
        {
            P(cas, "@newHead", newHead);
            P(cas, "@token", batch.FencingToken);
            P(cas, "@now", Now());
            P(cas, "@id", id);
            P(cas, "@expected", batch.ExpectedHeadOffset);
            if (await cas.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await ThrowCasFailureAsync(conn, tx, id, batch.FencingToken, resource, ct).ConfigureAwait(false);
            }
        }

        // (c) events (PK (stream_id, event_offset) makes a duplicate impossible), (d) row images.
        for (var i = 0; i < batch.Events.Count; i += EventInsertChunk)
        {
            await InsertEventsAsync(conn, tx, batch.Events.Skip(i).Take(EventInsertChunk).ToList(), ct).ConfigureAwait(false);
        }

        await WriteImagesAsync(conn, tx, id, batch.ImageWrites, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> UpsertWatermarkAsync(DbConnection conn, DbTransaction tx, CdcAppendBatch batch, CancellationToken ct)
    {
        const string insertColumns = "source, domain_name, schema_name, table_name, watermark, fencing_token, updated_at, updated_by";
        string sql = dialect switch
        {
            CdcSqlDialect.Sqlite => $@"
                INSERT INTO {_watermarks} ({insertColumns})
                VALUES (@source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy)
                ON CONFLICT(source, domain_name, schema_name, table_name) DO UPDATE SET
                    watermark = MAX(watermark, excluded.watermark),
                    fencing_token = excluded.fencing_token,
                    updated_at = excluded.updated_at,
                    updated_by = excluded.updated_by
                WHERE excluded.fencing_token >= {_watermarks}.fencing_token;",
            CdcSqlDialect.PostgreSql => $@"
                INSERT INTO {_watermarks} AS t ({insertColumns})
                VALUES (@source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy)
                ON CONFLICT (source, domain_name, schema_name, table_name) DO UPDATE SET
                    watermark = GREATEST(t.watermark, EXCLUDED.watermark),
                    fencing_token = EXCLUDED.fencing_token,
                    updated_at = EXCLUDED.updated_at,
                    updated_by = EXCLUDED.updated_by
                WHERE EXCLUDED.fencing_token >= t.fencing_token;",
            _ => $@"
                DECLARE @affected INT = 0;
                UPDATE {_watermarks} WITH (UPDLOCK, HOLDLOCK)
                   SET watermark = IIF(watermark > @watermark, watermark, @watermark),
                       fencing_token = @token, updated_at = @updatedAt, updated_by = @updatedBy
                 WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table
                   AND fencing_token <= @token;
                SET @affected = @@ROWCOUNT;
                IF @affected = 0
                BEGIN
                    INSERT INTO {_watermarks} ({insertColumns})
                    SELECT @source, @domain, @schema, @table, @watermark, @token, @updatedAt, @updatedBy
                     WHERE NOT EXISTS (SELECT 1 FROM {_watermarks} WITH (UPDLOCK, HOLDLOCK)
                                        WHERE source = @source AND domain_name = @domain AND schema_name = @schema AND table_name = @table);
                    SET @affected = @@ROWCOUNT;
                END
                SELECT @affected;"
        };

        await using var cmd = Cmd(conn, tx, sql);
        P(cmd, "@source", batch.Source);
        P(cmd, "@domain", batch.Table.Domain);
        P(cmd, "@schema", batch.Table.Schema);
        P(cmd, "@table", batch.Table.TableName);
        P(cmd, "@watermark", batch.NewWatermark);
        P(cmd, "@token", batch.FencingToken);
        P(cmd, "@updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        P(cmd, "@updatedBy", Environment.MachineName);
        if (dialect == CdcSqlDialect.SqlServer)
        {
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private async Task ThrowCasFailureAsync(DbConnection conn, DbTransaction tx, string id, long token, string resource, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx, $"SELECT producer_fencing_token FROM {_streams} WHERE stream_id = @id");
        P(cmd, "@id", id);
        var stored = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (stored is not null and not DBNull && Convert.ToInt64(stored, CultureInfo.InvariantCulture) > token)
        {
            throw new FencingTokenRejectedException(resource, token);
        }

        throw new CdcStreamConflictException();
    }

    private async Task InsertEventsAsync(DbConnection conn, DbTransaction tx, IReadOnlyList<CdcStreamEventRecord> events, CancellationToken ct)
    {
        var sb = new StringBuilder($@"INSERT INTO {_events}
            (stream_id, event_offset, change_version, kind, operation, tenant_scope, tenant_id, prior_tenant_id, dek_id, payload, produced_ms) VALUES ");
        await using var cmd = Cmd(conn, tx, string.Empty);
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            sb.Append(i > 0 ? "," : string.Empty)
              .Append($"(@sid{n}, @off{n}, @ver{n}, @kind{n}, @op{n}, @scope{n}, @tenant{n}, @prior{n}, @dek{n}, @payload{n}, @prod{n})");
            P(cmd, "@sid" + n, e.StreamId.Value);
            P(cmd, "@off" + n, e.Offset);
            P(cmd, "@ver" + n, e.ChangeVersion);
            P(cmd, "@kind" + n, (int)e.Kind);
            P(cmd, "@op" + n, e.Operation is { } op ? (int)op : null);
            P(cmd, "@scope" + n, (int)e.TenantScope);
            P(cmd, "@tenant" + n, e.TenantId);
            P(cmd, "@prior" + n, e.PriorTenantId);
            P(cmd, "@dek" + n, e.Payload.DekId);
            P(cmd, "@payload" + n, e.Payload.Data);
            P(cmd, "@prod" + n, Ms(e.ProducedAt));
        }

        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteImagesAsync(DbConnection conn, DbTransaction tx, string streamId, IReadOnlyList<CdcRowImageWrite> writes, CancellationToken ct)
    {
        if (writes.Count == 0)
        {
            return;
        }

        // Last write per fingerprint wins; the whole set is removed first, then the surviving images are inserted.
        var latest = new Dictionary<string, CdcRowImageWrite>(StringComparer.Ordinal);
        foreach (var w in writes)
        {
            latest[Convert.ToHexString(w.KeyFingerprint)] = w;
        }

        var all = latest.Values.ToList();
        for (var i = 0; i < all.Count; i += InListChunk)
        {
            var chunk = all.Skip(i).Take(InListChunk).Select(w => (object)w.KeyFingerprint).ToList();
            await using var del = Cmd(conn, tx, string.Empty);
            P(del, "@sid", streamId);
            del.CommandText = $"DELETE FROM {_images} WHERE stream_id = @sid AND key_fingerprint IN ({InList(del, "fp", chunk)})";
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var inserts = all.Where(w => w.Image is not null).ToList();
        var now = Now();
        for (var i = 0; i < inserts.Count; i += ImageInsertChunk)
        {
            var chunk = inserts.Skip(i).Take(ImageInsertChunk).ToList();
            var sb = new StringBuilder($"INSERT INTO {_images} (stream_id, key_fingerprint, tenant_scope, tenant_id, dek_id, image, event_offset, updated_ms) VALUES ");
            await using var cmd = Cmd(conn, tx, string.Empty);
            P(cmd, "@sid", streamId);
            P(cmd, "@now", now);
            for (var j = 0; j < chunk.Count; j++)
            {
                var w = chunk[j];
                var n = j.ToString(CultureInfo.InvariantCulture);
                sb.Append(j > 0 ? "," : string.Empty).Append($"(@sid, @fp{n}, @scope{n}, @tenant{n}, @dek{n}, @img{n}, @off{n}, @now)");
                P(cmd, "@fp" + n, w.KeyFingerprint);
                P(cmd, "@scope" + n, (int)w.TenantScope);
                P(cmd, "@tenant" + n, w.TenantId);
                P(cmd, "@dek" + n, w.Image!.DekId);
                P(cmd, "@img" + n, w.Image.Data);
                P(cmd, "@off" + n, w.Offset);
            }

            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ reads

    public async Task<IReadOnlyList<CdcRowImageRecord>> GetRowImagesAsync(DbConnection conn, CdcStreamId id, IReadOnlyList<byte[]> fingerprints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        var result = new List<CdcRowImageRecord>();
        var distinct = fingerprints.GroupBy(Convert.ToHexString).Select(g => g.First()).ToList();
        for (var i = 0; i < distinct.Count; i += InListChunk)
        {
            var chunk = distinct.Skip(i).Take(InListChunk).Select(f => (object)f).ToList();
            await using var cmd = Cmd(conn, null, string.Empty);
            P(cmd, "@sid", id.Value ?? string.Empty);
            cmd.CommandText = $@"SELECT key_fingerprint, tenant_scope, tenant_id, dek_id, image, event_offset FROM {_images}
                                  WHERE stream_id = @sid AND key_fingerprint IN ({InList(cmd, "fp", chunk)})";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new CdcRowImageRecord(B(r, 0), (CdcTenantScope)(int)L(r, 1), S(r, 2), new CdcProtectedBlob(r.GetString(3), B(r, 4)), L(r, 5)));
            }
        }

        return result;
    }

    /// <summary>
    /// Events are read first, then tail and head. A purge or append committing in between can only make the returned tail/head newer
    /// than the rows, which a reader detects as a gap (offset below tail) or as more to read (head beyond the last row) - never as silent loss.
    /// </summary>
    public async Task<CdcReadResult> ReadAsync(DbConnection conn, CdcStreamId id, long fromOffsetInclusive, int maxEvents, CancellationToken ct)
    {
        var take = Math.Clamp(maxEvents, 1, MaxReadBatch);
        var sid = id.Value ?? string.Empty;
        var cols = "event_offset, change_version, kind, operation, tenant_scope, tenant_id, prior_tenant_id, dek_id, payload, produced_ms";
        var from = $"FROM {_events} WHERE stream_id = @sid AND event_offset >= @from ORDER BY event_offset";
        var sql = dialect == CdcSqlDialect.SqlServer
            ? $"SELECT TOP (@take) {cols} {from}"
            : $"SELECT {cols} {from} LIMIT @take";

        var events = new List<CdcStreamEventRecord>();
        await using (var cmd = Cmd(conn, null, sql))
        {
            P(cmd, "@sid", sid);
            P(cmd, "@from", fromOffsetInclusive);
            P(cmd, "@take", take);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                events.Add(new CdcStreamEventRecord(id, L(r, 0), L(r, 1), (CdcStreamEventKind)(int)L(r, 2),
                    r.IsDBNull(3) ? null : (CdcOperation)(int)L(r, 3), (CdcTenantScope)(int)L(r, 4), S(r, 5), S(r, 6),
                    new CdcProtectedBlob(r.GetString(7), B(r, 8)), DateTimeOffset.FromUnixTimeMilliseconds(L(r, 9))));
            }
        }

        await using var bounds = Cmd(conn, null, $"SELECT tail_offset, head_offset FROM {_streams} WHERE stream_id = @sid");
        P(bounds, "@sid", sid);
        await using var br = await bounds.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await br.ReadAsync(ct).ConfigureAwait(false) ? new CdcReadResult(L(br, 0), L(br, 1), events) : new CdcReadResult(1, 0, []);
    }

    // -------------------------------------------------------------- retention

    public async Task<long> PurgeAsync(DbConnection conn, CdcStreamId id, long newTailOffset, int maxRows, long fencingToken, CancellationToken ct)
    {
        var sid = id.Value ?? string.Empty;
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The fenced UPDATE takes the stream row lock first, so tail/head below are stable for this transaction.
        await using (var fence = Cmd(conn, tx, $"UPDATE {_streams} SET retention_fencing_token = @token, updated_ms = @now WHERE stream_id = @id AND retention_fencing_token <= @token"))
        {
            P(fence, "@token", fencingToken);
            P(fence, "@now", Now());
            P(fence, "@id", sid);
            if (await fence.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            {
                await using var exists = Cmd(conn, tx, $"SELECT 1 FROM {_streams} WHERE stream_id = @id");
                P(exists, "@id", sid);
                if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                {
                    return 0;
                }

                throw new FencingTokenRejectedException("cdc-stream-retention:" + sid, fencingToken);
            }
        }

        long tail;
        long head;
        await using (var read = Cmd(conn, tx, $"SELECT tail_offset, head_offset FROM {_streams} WHERE stream_id = @id"))
        {
            P(read, "@id", sid);
            await using var r = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await r.ReadAsync(ct).ConfigureAwait(false);
            tail = L(r, 0);
            head = L(r, 1);
        }

        var effective = Math.Min(Math.Min(newTailOffset, head + 1), tail + Math.Max(1, maxRows));
        if (effective > tail)
        {
            await using (var del = Cmd(conn, tx, $"DELETE FROM {_events} WHERE stream_id = @id AND event_offset < @eff"))
            {
                P(del, "@id", sid);
                P(del, "@eff", effective);
                await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var move = Cmd(conn, tx, $"UPDATE {_streams} SET tail_offset = @eff WHERE stream_id = @id");
            P(move, "@eff", effective);
            P(move, "@id", sid);
            await move.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tail = effective;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return tail;
    }

    public async Task<int> PurgeImagesAsync(DbConnection conn, CdcStreamId id, int maxRows, CancellationToken ct)
    {
        var limit = Math.Max(1, maxRows);
        string sql = dialect switch
        {
            CdcSqlDialect.SqlServer => $"DELETE TOP (@n) FROM {_images} WHERE stream_id = @sid",
            CdcSqlDialect.PostgreSql => $@"DELETE FROM {_images} WHERE ctid IN (SELECT ctid FROM {_images} WHERE stream_id = @sid LIMIT @n)",
            _ => $@"DELETE FROM {_images} WHERE rowid IN (SELECT rowid FROM {_images} WHERE stream_id = @sid LIMIT @n)"
        };
        await using var cmd = Cmd(conn, null, sql);
        P(cmd, "@sid", id.Value ?? string.Empty);
        P(cmd, "@n", limit);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DiscardUnreadableAsync(DbConnection conn, CdcStreamId id, IReadOnlyCollection<string> dekIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dekIds);
        var deks = dekIds.Distinct(StringComparer.Ordinal).Select(d => (object)d).ToList();
        if (deks.Count == 0)
        {
            return 0;
        }

        var removed = 0;
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < deks.Count; i += InListChunk)
        {
            var chunk = deks.Skip(i).Take(InListChunk).ToList();
            foreach (var (table, counted) in new[] { (_events, true), (_images, true), (_keys, false) })
            {
                await using var cmd = Cmd(conn, tx, string.Empty);
                P(cmd, "@sid", id.Value ?? string.Empty);
                cmd.CommandText = $"DELETE FROM {table} WHERE stream_id = @sid AND dek_id IN ({InList(cmd, "d", chunk)})";
                var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (counted)
                {
                    removed += n;
                }
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return removed;
    }

    // ------------------------------------------------------------------- keys

    public async Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(DbConnection conn, CdcStreamId id, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $@"SELECT dek_id, purpose, kek_id, wrapped_key, created_ms, retired_ms FROM {_keys}
                                                  WHERE stream_id = @sid ORDER BY created_ms, dek_id");
        P(cmd, "@sid", id.Value ?? string.Empty);
        var result = new List<CdcWrappedKey>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new CdcWrappedKey(id, r.GetString(0), (CdcKeyPurpose)(int)L(r, 1), r.GetString(2), B(r, 3),
                DateTimeOffset.FromUnixTimeMilliseconds(L(r, 4)), r.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(L(r, 5))));
        }

        return result;
    }

    public async Task<bool> TryAddKeyAsync(DbConnection conn, CdcWrappedKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var cmd = Cmd(conn, null, $@"INSERT INTO {_keys} (stream_id, dek_id, purpose, kek_id, wrapped_key, created_ms, retired_ms)
                                                  VALUES (@sid, @dek, @purpose, @kek, @wrapped, @created, @retired)");
        P(cmd, "@sid", key.StreamId.Value);
        P(cmd, "@dek", key.DekId);
        P(cmd, "@purpose", (int)key.Purpose);
        P(cmd, "@kek", key.KekId);
        P(cmd, "@wrapped", key.WrappedKey);
        P(cmd, "@created", Ms(key.CreatedAt));
        P(cmd, "@retired", key.RetiredAt is { } at ? Ms(at) : null);
        try
        {
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    public async Task RetireKeyAsync(DbConnection conn, CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $"UPDATE {_keys} SET retired_ms = @at WHERE stream_id = @sid AND dek_id = @dek AND retired_ms IS NULL");
        P(cmd, "@at", Ms(at));
        P(cmd, "@sid", id.Value ?? string.Empty);
        P(cmd, "@dek", dekId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ReplaceWrappingAsync(DbConnection conn, CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $"UPDATE {_keys} SET kek_id = @kek, wrapped_key = @wrapped WHERE stream_id = @sid AND dek_id = @dek");
        P(cmd, "@kek", kekId);
        P(cmd, "@wrapped", wrappedKey);
        P(cmd, "@sid", id.Value ?? string.Empty);
        P(cmd, "@dek", dekId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnreferencedPayloadKeysAsync(DbConnection conn, CdcStreamId id, CancellationToken ct)
    {
        // Only retired payload keys: an active key without events yet is still needed by the producer.
        var keysName = _keys[(_keys.IndexOf('.', StringComparison.Ordinal) + 1)..];
        await using var cmd = Cmd(conn, null, $@"DELETE FROM {_keys}
             WHERE stream_id = @sid AND purpose = @purpose AND retired_ms IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM {_events} e WHERE e.stream_id = {keysName}.stream_id AND e.dek_id = {keysName}.dek_id)");
        P(cmd, "@sid", id.Value ?? string.Empty);
        P(cmd, "@purpose", (int)CdcKeyPurpose.Payload);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByKekAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, $"SELECT kek_id, COUNT(*) FROM {_keys} GROUP BY kek_id");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            result[r.GetString(0)] = (int)L(r, 1);
        }

        return result;
    }
}
