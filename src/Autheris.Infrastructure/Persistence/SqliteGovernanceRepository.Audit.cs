using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository : IAuditChainExportSource
{
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await RecordAuditEventInternalAsync(entry, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    private async Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct)
    {
        using var tx = _connection.BeginTransaction(System.Data.IsolationLevel.Serializable);

        // SEC H-17: the DB tail is compared with the in-memory reference instead of being adopted blindly.
        var (dbTailHash, dbTailSeq) = ReadAuditTail(tx);
        var effectiveDbHash = dbTailHash ?? AuditGenesisHash;
        if (!FixedTimeEqualsString(effectiveDbHash, _lastAuditHash) || dbTailSeq != _lastAuditSeq)
        {
            var anchor = TryLoadVerifiedAnchor(out _);
            var explainedByAnchor = anchor != null
                                    && dbTailSeq > _lastAuditSeq
                                    && anchor.Sequence == dbTailSeq
                                    && FixedTimeEqualsString(anchor.EntryHash, effectiveDbHash);
            if (!explainedByAnchor)
            {
                FlagAuditChainViolation(
                    $"Audit chain tail in DB (seq {dbTailSeq}) diverges from the in-memory reference (seq {_lastAuditSeq}) without a matching signed anchor - possible truncation or rewrite.");
            }

            // Keep the chain linear; verification stays failed while a violation is flagged.
            _lastAuditHash = effectiveDbHash;
            _lastAuditSeq = dbTailSeq;
        }

        var sequence = _lastAuditSeq + 1;
        entry.PrevHash = _lastAuditHash;
        entry.EntryHash = ComputeAuditEntryHash(
            sequence,
            entry.Id.ToString(),
            entry.PrevHash,
            entry.OccurredAt,
            entry.EventType,
            entry.ActorSid.Value,
            entry.TargetTable,
            entry.TargetColumn,
            entry.Decision,
            entry.TraceId,
            entry.DetailsJson,
            entry.TenantId.Value);

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq)
                                VALUES (@id, @occ, @event, @actor, @target, @col, @dec, @trace, @det, @prev, @hash, @tenantId, @seq)";
            cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("@occ", entry.OccurredAt.ToString("O"));
            cmd.Parameters.AddWithValue("@event", entry.EventType);
            cmd.Parameters.AddWithValue("@actor", entry.ActorSid.Value);
            cmd.Parameters.AddWithValue("@target", entry.TargetTable);
            cmd.Parameters.AddWithValue("@col", (object?)entry.TargetColumn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dec", entry.Decision);
            cmd.Parameters.AddWithValue("@trace", entry.TraceId);
            cmd.Parameters.AddWithValue("@det", entry.DetailsJson);
            cmd.Parameters.AddWithValue("@prev", entry.PrevHash);
            cmd.Parameters.AddWithValue("@hash", entry.EntryHash);
            cmd.Parameters.AddWithValue("@tenantId", entry.TenantId.Value);
            cmd.Parameters.AddWithValue("@seq", sequence);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // SEC H-17: advance the external signed anchor while the write lock is still held. An anchor is never
        // advanced while a violation is flagged (it would otherwise "launder" a truncated chain).
        var anchorAdvanced = false;
        if (Volatile.Read(ref _auditChainViolation) == null)
        {
            try
            {
                _auditAnchorStore.Save(CreateSignedAnchor(sequence, entry.EntryHash));
                anchorAdvanced = true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist the external audit chain anchor (seq {Sequence}).", sequence);
            }
        }

        try
        {
            await tx.CommitAsync(ct);
        }
        catch
        {
            if (anchorAdvanced)
            {
                try
                {
                    // Roll the anchor back to the last committed state.
                    _auditAnchorStore.Save(CreateSignedAnchor(_lastAuditSeq, _lastAuditHash));
                }
                catch (Exception restoreEx)
                {
                    _logger?.LogError(restoreEx, "Failed to restore the previous audit chain anchor after a failed commit.");
                }
            }
            throw;
        }

        _lastAuditHash = entry.EntryHash;
        _lastAuditSeq = sequence;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditLogEntry>();
            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder(@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                FROM AUDIT_LOG_ENTRIES");
            if (tenantId.HasValue)
            {
                sql.Append(" WHERE tenant_id = @tenantId");
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }
            sql.Append(" ORDER BY rowid ASC LIMIT @lim");
            cmd.CommandText = sql.ToString();
            cmd.Parameters.AddWithValue("@lim", limit);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AuditLogEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    OccurredAt = DateTimeOffset.Parse(reader.GetString(1)),
                    EventType = reader.GetString(2),
                    ActorSid = new Sid(reader.GetString(3)),
                    TargetTable = reader.GetString(4),
                    TargetColumn = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Decision = reader.GetString(6),
                    TraceId = reader.GetString(7),
                    DetailsJson = reader.GetString(8),
                    PrevHash = reader.GetString(9),
                    EntryHash = reader.GetString(10),
                    TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant)
                });
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditLogEntry>();
            using var cmd = _connection.CreateCommand();
            var sql = new StringBuilder(@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                               decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                        FROM AUDIT_LOG_ENTRIES
                                        WHERE 1=1");

            if (!string.IsNullOrWhiteSpace(targetTable))
            {
                sql.Append(" AND target_table = @targetTable");
                cmd.Parameters.AddWithValue("@targetTable", targetTable);
            }
            if (actorSid.HasValue && !string.IsNullOrWhiteSpace(actorSid.Value.Value))
            {
                sql.Append(" AND actor_sid = @actorSid");
                cmd.Parameters.AddWithValue("@actorSid", actorSid.Value.Value);
            }
            if (since.HasValue)
            {
                sql.Append(" AND occurred_at >= @since");
                cmd.Parameters.AddWithValue("@since", since.Value.ToString("O"));
            }
            if (tenantId.HasValue)
            {
                sql.Append(" AND tenant_id = @tenantId");
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            sql.Append(" ORDER BY occurred_at DESC LIMIT @lim");
            cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 5000));
            cmd.CommandText = sql.ToString();

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AuditLogEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    OccurredAt = DateTimeOffset.Parse(reader.GetString(1)),
                    EventType = reader.GetString(2),
                    ActorSid = new Sid(reader.GetString(3)),
                    TargetTable = reader.GetString(4),
                    TargetColumn = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Decision = reader.GetString(6),
                    TraceId = reader.GetString(7),
                    DetailsJson = reader.GetString(8),
                    PrevHash = reader.GetString(9),
                    EntryHash = reader.GetString(10),
                    TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant)
                });
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var violation = Volatile.Read(ref _auditChainViolation);
            if (violation != null)
            {
                _logger?.LogCritical("Audit hash chain verification failed: {Violation}", violation);
                return false;
            }

            var anchor = TryLoadVerifiedAnchor(out var anchorInvalid);
            if (anchorInvalid)
            {
                FlagAuditChainViolation("External audit chain anchor has an invalid signature or format.");
                return false;
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                                FROM AUDIT_LOG_ENTRIES
                                ORDER BY rowid ASC";

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var expectedPrevHash = AuditGenesisHash;
            long position = 0;
            var sequencedSectionStarted = false;
            var anchorEntryMatched = anchor == null || anchor.Sequence == 0;

            while (await reader.ReadAsync(ct))
            {
                position++;
                var id = reader.GetString(0);
                var occurredAt = reader.GetString(1);
                var eventType = reader.GetString(2);
                var actorSid = reader.GetString(3);
                var targetTable = reader.GetString(4);
                var targetColumn = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var decision = reader.GetString(6);
                var traceId = reader.GetString(7);
                var detailsJson = reader.GetString(8);
                var prevHash = reader.GetString(9);
                var entryHash = reader.GetString(10);
                var tenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant.Value : reader.GetString(11);
                long? seq = reader.IsDBNull(12) ? null : reader.GetInt64(12);

                if (!FixedTimeEqualsString(prevHash, expectedPrevHash))
                {
                    return false; // Broken chain!
                }

                // SEC H-17: sequence numbers must be gap-free and equal the ordinal position.
                if (seq.HasValue)
                {
                    if (seq.Value != position)
                    {
                        return false;
                    }
                    sequencedSectionStarted = true;
                }
                else if (sequencedSectionStarted)
                {
                    return false; // Unsequenced (legacy-format) row injected after the sequenced section
                }

                var parsedOccurredAt = DateTimeOffset.Parse(occurredAt);
                var computedHash = ComputeAuditEntryHash(seq, id, prevHash, parsedOccurredAt, eventType, actorSid, targetTable, targetColumn, decision, traceId, detailsJson, tenantId);

                if (!FixedTimeEqualsString(entryHash, computedHash))
                {
                    return false; // Tampered payload!
                }

                if (anchor != null && anchor.Sequence == position)
                {
                    anchorEntryMatched = FixedTimeEqualsString(entryHash, anchor.EntryHash);
                }

                expectedPrevHash = entryHash;
            }

            // SEC H-17: compare with the external signed anchor (detects tail truncation and total deletion,
            // also across restarts).
            if (anchor != null)
            {
                if (position < anchor.Sequence)
                {
                    FlagAuditChainViolation($"Audit chain truncated: DB ends at seq {position}, signed anchor at seq {anchor.Sequence}.");
                    return false;
                }

                if (!anchorEntryMatched)
                {
                    FlagAuditChainViolation($"Audit chain entry at anchored seq {anchor.Sequence} does not match the signed anchor hash.");
                    return false;
                }
            }

            // Tail truncation detection against the in-memory reference of this process.
            if (_lastAuditHash != AuditGenesisHash &&
                (!FixedTimeEqualsString(expectedPrevHash, _lastAuditHash) || position != _lastAuditSeq))
            {
                var explainedByAnchor = anchor != null
                                        && position > _lastAuditSeq
                                        && anchor.Sequence == position
                                        && FixedTimeEqualsString(anchor.EntryHash, expectedPrevHash);
                if (!explainedByAnchor)
                {
                    FlagAuditChainViolation($"Audit chain tail (seq {position}) does not match the in-memory reference (seq {_lastAuditSeq}).");
                    return false; // Tail truncation detected!
                }
            }

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            long firstRowId;
            long lastRowId;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT MIN(rowid), MAX(rowid) FROM AUDIT_LOG_ENTRIES
                                    WHERE occurred_at >= @from AND occurred_at <= @to";
                cmd.Parameters.AddWithValue("@from", windowFrom.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("@to", windowTo.ToUniversalTime().ToString("O"));
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    return null;
                }
                firstRowId = reader.GetInt64(0);
                lastRowId = reader.GetInt64(1);
            }

            using var countCmd = _connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";
            countCmd.Parameters.AddWithValue("@a", firstRowId);
            countCmd.Parameters.AddWithValue("@b", lastRowId);
            var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            return new AuditChainRange(firstRowId, lastRowId, count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<AuditChainRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                FROM AUDIT_LOG_ENTRIES
                                WHERE rowid > @after AND rowid <= @last
                                ORDER BY rowid ASC
                                LIMIT @lim";
            cmd.Parameters.AddWithValue("@after", afterRowId);
            cmd.Parameters.AddWithValue("@last", lastRowIdInclusive);
            cmd.Parameters.AddWithValue("@lim", Math.Clamp(pageSize, 1, 10000));

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                long? seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                var entry = new AuditLogEntry
                {
                    Id = Guid.Parse(reader.GetString(2)),
                    OccurredAt = DateTimeOffset.Parse(reader.GetString(3)),
                    EventType = reader.GetString(4),
                    ActorSid = new Sid(reader.GetString(5)),
                    TargetTable = reader.GetString(6),
                    TargetColumn = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Decision = reader.GetString(8),
                    TraceId = reader.GetString(9),
                    DetailsJson = reader.GetString(10),
                    PrevHash = reader.GetString(11),
                    EntryHash = reader.GetString(12),
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(13), out var tid) ? tid : TenantId.LegacySingleTenant)
                };
                list.Add(new AuditChainRecord(reader.GetInt64(0), seq, entry));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    /// <summary>Last entry hash and its (explicit or ordinal) sequence number; hash is null for an empty log.</summary>
    private (string? Hash, long Sequence) ReadAuditTail(SqliteTransaction? tx)
    {
        string? hash = null;
        long? seq = null;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                hash = reader.GetString(0);
                seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }

        if (hash == null)
        {
            return (null, 0);
        }

        if (seq.HasValue)
        {
            return (hash, seq.Value);
        }

        using var countCmd = _connection.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";
        var count = Convert.ToInt64(countCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return (hash, count);
    }

    private string ComputeAuditEntryHash(
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId)
    {
        var payload = $"{id}|{prevHash}|{occurredAt:O}|{EscapeField(eventType)}|{EscapeField(actorSid)}|{EscapeField(targetTable)}|{EscapeField(targetColumn)}|{EscapeField(decision)}|{EscapeField(traceId)}|{EscapeField(detailsJson)}|{EscapeField(tenantId)}";
        if (sequence.HasValue)
        {
            // SEC H-17: v2 payload binds the gap-free sequence number into the HMAC.
            payload = $"v2|{sequence.Value}|{payload}";
        }

        Span<byte> hashBytes = stackalloc byte[32];
        HMACSHA256.HashData(_auditHmacKey, Encoding.UTF8.GetBytes(payload), hashBytes);
        return Convert.ToHexString(hashBytes);
    }

    private AuditChainAnchor CreateSignedAnchor(long sequence, string entryHash)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        return new AuditChainAnchor(sequence, entryHash, updatedAt, ComputeAnchorSignature(sequence, entryHash, updatedAt));
    }

    private string ComputeAnchorSignature(long sequence, string entryHash, DateTimeOffset updatedAt)
    {
        var data = Encoding.UTF8.GetBytes($"anchor-v1|{sequence}|{entryHash}|{updatedAt.ToUniversalTime():O}");
        return Convert.ToHexString(HMACSHA256.HashData(_auditAnchorKey, data));
    }

    private AuditChainAnchor? TryLoadVerifiedAnchor(out bool invalid)
    {
        invalid = false;
        AuditChainAnchor? anchor;
        try
        {
            anchor = _auditAnchorStore.Load();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read the external audit chain anchor.");
            invalid = true;
            return null;
        }

        if (anchor == null)
        {
            return null;
        }

        if (anchor.Sequence < 0 || string.IsNullOrEmpty(anchor.EntryHash) || string.IsNullOrEmpty(anchor.Signature))
        {
            invalid = true;
            return null;
        }

        var expected = ComputeAnchorSignature(anchor.Sequence, anchor.EntryHash, anchor.UpdatedAt);
        if (!FixedTimeEqualsString(expected, anchor.Signature))
        {
            invalid = true;
            return null;
        }

        return anchor;
    }

    private void InitializeAuditChainAnchor(bool isDevOrTest)
    {
        var anchor = TryLoadVerifiedAnchor(out var invalid);
        if (invalid)
        {
            FlagAuditChainViolation("External audit chain anchor has an invalid signature or format at startup.");
            return;
        }

        if (anchor == null)
        {
            if (_lastAuditSeq > 0)
            {
                if (!isDevOrTest)
                {
                    FlagAuditChainViolation(
                        $"Audit DB has {_lastAuditSeq} entries, but external audit chain anchor is missing. " +
                        "Potential truncation or unauthorized deletion detected.");
                    return;
                }
                _logger?.LogWarning("No external audit chain anchor found; initialising it from the current DB tail (seq {Sequence}). Trust-on-first-use.", _lastAuditSeq);
            }
            if (!isDevOrTest && _auditAnchorStore is InMemoryAuditChainAnchorStore)
            {
                _logger?.LogWarning("Audit chain anchor is only held in memory; configure Audit:ChainAnchorPath on a separate volume.");
            }

            try
            {
                _auditAnchorStore.Save(CreateSignedAnchor(_lastAuditSeq, _lastAuditHash));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialise the external audit chain anchor.");
            }
            return;
        }

        if (_lastAuditSeq < anchor.Sequence)
        {
            FlagAuditChainViolation($"Audit DB tail (seq {_lastAuditSeq}) is behind the signed anchor (seq {anchor.Sequence}): truncation or deletion of audit entries detected.");
        }
        else if (_lastAuditSeq == anchor.Sequence && !FixedTimeEqualsString(_lastAuditHash, anchor.EntryHash))
        {
            FlagAuditChainViolation($"Audit DB tail hash at seq {anchor.Sequence} does not match the signed anchor.");
        }
    }

    private static IAuditChainAnchorStore CreateDefaultAuditAnchorStore(string connectionString, bool isMemory, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new FileAuditChainAnchorStore(configuredPath);
        }

        string dataSource;
        try
        {
            dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch
        {
            dataSource = connectionString;
        }

        if (isMemory || string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:")
        {
            return new InMemoryAuditChainAnchorStore();
        }

        return new FileAuditChainAnchorStore(dataSource + ".audit-anchor.json");
    }

    private void FlagAuditChainViolation(string reason)
    {
        if (Interlocked.CompareExchange(ref _auditChainViolation, reason, null) == null)
        {
            _logger?.LogCritical("SECURITY ALERT - audit hash chain integrity violation: {Reason}", reason);
        }
    }

    private static bool FixedTimeEqualsString(string? a, string? b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));

    private static string EscapeField(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("|", "\\|");
    }
}
