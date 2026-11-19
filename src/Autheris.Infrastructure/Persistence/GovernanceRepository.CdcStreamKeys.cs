namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;

public partial class GovernanceRepository : ICdcStreamKeyStore
{
    public async Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.GetKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryAddKeyAsync(CdcWrappedKey key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.TryAddKeyAsync(conn, key, ct).ConfigureAwait(false);
    }

    public async Task RetireKeyAsync(CdcStreamId id, string dekId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await CdcCore.RetireKeyAsync(conn, id, dekId, at, ct).ConfigureAwait(false);
    }

    public async Task ReplaceWrappingAsync(CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await CdcCore.ReplaceWrappingAsync(conn, id, dekId, kekId, wrappedKey, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnreferencedPayloadKeysAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.DeleteUnreferencedPayloadKeysAsync(conn, id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByKekAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await CdcCore.CountByKekAsync(conn, ct).ConfigureAwait(false);
    }

    private sealed partial class CdcStreamSql
    {
        // ------------------------------------------------------------------- keys

        public async Task<IReadOnlyList<CdcWrappedKey>> GetKeysAsync(DbConnection conn, CdcStreamId id, CancellationToken ct)
        {
            await using var cmd = Cmd(conn, null, @"SELECT dek_id, purpose, kek_id, wrapped_key, created_ms, retired_ms FROM %keys%
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
            await using var cmd = Cmd(conn, null, @"INSERT INTO %keys% (stream_id, dek_id, purpose, kek_id, wrapped_key, created_ms, retired_ms)
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
            await using var cmd = Cmd(conn, null, "UPDATE %keys% SET retired_ms = @at WHERE stream_id = @sid AND dek_id = @dek AND retired_ms IS NULL");
            P(cmd, "@at", Ms(at));
            P(cmd, "@sid", id.Value ?? string.Empty);
            P(cmd, "@dek", dekId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task ReplaceWrappingAsync(DbConnection conn, CdcStreamId id, string dekId, string kekId, byte[] wrappedKey, CancellationToken ct)
        {
            await using var cmd = Cmd(conn, null, "UPDATE %keys% SET kek_id = @kek, wrapped_key = @wrapped WHERE stream_id = @sid AND dek_id = @dek");
            P(cmd, "@kek", kekId);
            P(cmd, "@wrapped", wrappedKey);
            P(cmd, "@sid", id.Value ?? string.Empty);
            P(cmd, "@dek", dekId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<int> DeleteUnreferencedPayloadKeysAsync(DbConnection conn, CdcStreamId id, CancellationToken ct)
        {
            // Only retired payload keys: an active key without events yet is still needed by the producer.
            await using var cmd = Cmd(conn, null, @"DELETE FROM %keys%
                 WHERE stream_id = @sid AND purpose = @purpose AND retired_ms IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM %events% e WHERE e.stream_id = autheris_cdc_stream_keys.stream_id AND e.dek_id = autheris_cdc_stream_keys.dek_id)");
            P(cmd, "@sid", id.Value ?? string.Empty);
            P(cmd, "@purpose", (int)CdcKeyPurpose.Payload);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyDictionary<string, int>> CountByKekAsync(DbConnection conn, CancellationToken ct)
        {
            await using var cmd = Cmd(conn, null, "SELECT kek_id, COUNT(*) FROM %keys% GROUP BY kek_id");
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                result[r.GetString(0)] = (int)L(r, 1);
            }

            return result;
        }
    }
}
