using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
{
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        // Tier-A: Security critical mutations, admin actions, or denied requests are synchronous (Fail-Closed)
        bool isTierA = string.Equals(entry.Decision, "DENY", StringComparison.OrdinalIgnoreCase) ||
                       (!string.Equals(entry.EventType, "TABLE_QUERY", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(entry.EventType, "WEBSQL_QUERY", StringComparison.OrdinalIgnoreCase));

        // SEC R2-3: Audit:SynchronousQueryAudit commits query events before the request is answered (no crash window).
        if (isTierA || _options?.Audit?.SynchronousQueryAudit == true)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecordAuditEventInternalAsync(entry, ct).ConfigureAwait(false);
            }
            finally
            {
                _auditLock.Release();
            }
            return;
        }

        if (_isAuditPipelineFaulted)
        {
            _logger?.LogError("Audit pipeline is faulted; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new InvalidOperationException("Audit pipeline is in a faulted state (fail-closed).");
        }

        // Tier-B: High-volume query reads are enqueued to the bounded channel with timeout (fail-closed)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _auditChannel.Writer.WriteAsync(entry, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError("Audit channel saturated; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new InvalidOperationException("Audit pipeline saturated (fail-closed).");
        }
    }

    public async Task FlushAuditChannelAsync(CancellationToken ct = default)
    {
        var batch = new List<AuditLogEntry>();
        while (_auditChannel.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250)
            {
                await _auditLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await RecordAuditEventsBatchInternalAsync(batch, ct).ConfigureAwait(false);
                }
                finally
                {
                    _auditLock.Release();
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await RecordAuditEventsBatchInternalAsync(batch, ct).ConfigureAwait(false);
            }
            finally
            {
                _auditLock.Release();
            }
        }
    }

    private async Task ProcessAuditChannelAsync()
    {
        var batch = new List<AuditLogEntry>(250);
        var reader = _auditChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(_auditCts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                var deadline = DateTime.UtcNow.AddMilliseconds(10);

                while (batch.Count < 250 && (DateTime.UtcNow < deadline || batch.Count == 0))
                {
                    if (reader.TryRead(out var entry))
                    {
                        batch.Add(entry);
                    }
                    else
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    const int maxRetries = 3;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        await _auditLock.WaitAsync(_auditCts.Token).ConfigureAwait(false);
                        try
                        {
                            await RecordAuditEventsBatchInternalAsync(batch, _auditCts.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries (attempt {Attempt}/{MaxRetries})", batch.Count, attempt, maxRetries);
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(50 * attempt, _auditCts.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _isAuditPipelineFaulted = true;
                                _logger?.LogCritical(ex, "FATAL: Audit batch of {Count} entries failed permanently after 3 attempts. Setting audit pipeline to faulted (fail-closed).", batch.Count);
                            }
                        }
                        finally
                        {
                            _auditLock.Release();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining on shutdown
        }

        batch.Clear();
        while (reader.TryRead(out var entry))
        {
            batch.Add(entry);
            if (batch.Count >= 250)
            {
                await _auditLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await RecordAuditEventsBatchInternalAsync(batch, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _auditLock.Release();
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _auditLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await RecordAuditEventsBatchInternalAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _auditLock.Release();
            }
        }
    }

    private const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    private Task RecordAuditEventInternalAsync(AuditLogEntry entry, CancellationToken ct) =>
        RecordAuditEventsBatchInternalAsync([entry], ct);

    /// <summary>Review PG-1: resource name of the transaction-owned sp_getapplock that serialises audit chain writers across replicas.</summary>
    private const string AuditChainLockResource = "autheris:audit-chain";

    private const int AuditChainLockTimeoutMs = 30000;

    /// <summary>sp_getapplock did not grant the lock (timeout / deadlock victim); retried like a transient conflict.</summary>
    private sealed class AuditChainLockException(int code) : Exception($"sp_getapplock failed for '{AuditChainLockResource}' (return code {code}).")
    {
        public int Code { get; } = code;
    }

    private static bool IsRetryableAuditWriteError(SqlException ex) =>
        ex.Number is 2601 or 2627 or 1205 or 1222 || ex.IsTransient;

    private const int AuditWriteMaxAttempts = 5;

    private async Task RecordAuditEventsBatchInternalAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Review PG-1: concurrent writers (other replicas) are serialised by an application lock; serialization failures,
        // deadlocks and unique violations on seq are retried instead of failing the mutation or faulting the pipeline.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAuditBatchOnceAsync(batch, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < AuditWriteMaxAttempts &&
                                       ((ex is SqlException sqlEx && IsRetryableAuditWriteError(sqlEx)) ||
                                        (ex is AuditChainLockException lockEx && (lockEx.Code == -1 || lockEx.Code == -3))))
            {
                _logger?.LogWarning(ex, "Audit chain write conflict (attempt {Attempt}/{Max}); retrying.", attempt, AuditWriteMaxAttempts);
                await Task.Delay(Random.Shared.Next(10, 40) * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteAuditBatchOnceAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        // READ COMMITTED + application lock: the tail is read only after the previous writer has committed. (Under
        // SERIALIZABLE the snapshot would already be taken before the lock is acquired.)
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "sp_getapplock";
            lockCmd.CommandType = CommandType.StoredProcedure;
            lockCmd.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = AuditChainLockResource;
            lockCmd.Parameters.Add("@LockMode", SqlDbType.NVarChar, 32).Value = "Exclusive";
            lockCmd.Parameters.Add("@LockOwner", SqlDbType.NVarChar, 32).Value = "Transaction";
            lockCmd.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = AuditChainLockTimeoutMs;
            var pRet = lockCmd.Parameters.Add("@ret", SqlDbType.Int);
            pRet.Direction = ParameterDirection.ReturnValue;
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var lockResult = pRet.Value is int rc ? rc : -999;
            if (lockResult < 0)
            {
                throw new AuditChainLockException(lockResult);
            }
        }

        // The DB is the single source of truth for the tail. Another replica having advanced the chain is normal; a tail
        // that is BEHIND what this process has already seen (or the same sequence with another hash) means truncation or
        // rewriting, unless a signed anchor explains it.
        var (dbTailHash, dbTailSeq) = ReadAuditTail(tx);
        var effectiveDbHash = dbTailHash ?? AuditGenesisHash;
        var knownSeq = Interlocked.Read(ref _lastAuditSeq);
        var knownHash = _lastAuditHash;
        if (dbTailSeq < knownSeq || (dbTailSeq == knownSeq && !FixedTimeEqualsString(effectiveDbHash, knownHash)))
        {
            FlagAuditChainViolation(
                $"Audit chain tail in DB (seq {dbTailSeq}) is behind or differs from the last known tail (seq {knownSeq}) - possible truncation or rewrite.");
        }

        long lastSequence = dbTailSeq;
        string lastEntryHash = effectiveDbHash;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO AUDIT_LOG_ENTRIES (id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq)
                            VALUES (@id, @occ, @event, @actor, @target, @col, @dec, @trace, @det, @prev, @hash, @tenantId, @seq)";

        var pId = cmd.Parameters.Add("@id", SqlDbType.NVarChar, 128);
        var pOcc = cmd.Parameters.Add("@occ", SqlDbType.NVarChar, 64);
        var pEvent = cmd.Parameters.Add("@event", SqlDbType.NVarChar, 128);
        var pActor = cmd.Parameters.Add("@actor", SqlDbType.NVarChar, 256);
        var pTarget = cmd.Parameters.Add("@target", SqlDbType.NVarChar, 512);
        var pCol = cmd.Parameters.Add("@col", SqlDbType.NVarChar, 256);
        var pDec = cmd.Parameters.Add("@dec", SqlDbType.NVarChar, 64);
        var pTrace = cmd.Parameters.Add("@trace", SqlDbType.NVarChar, 128);
        var pDet = cmd.Parameters.Add("@det", SqlDbType.NVarChar, -1);
        var pPrev = cmd.Parameters.Add("@prev", SqlDbType.NVarChar, 128);
        var pHash = cmd.Parameters.Add("@hash", SqlDbType.NVarChar, 128);
        var pTenantId = cmd.Parameters.Add("@tenantId", SqlDbType.NVarChar, 128);
        var pSeq = cmd.Parameters.Add("@seq", SqlDbType.BigInt);

        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            long currentSequence = ++lastSequence;
            var currentPrevHash = lastEntryHash;

            var effectiveTenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value) ? TenantId.LegacySingleTenant.Value : entry.TenantId.Value;

            var entryHash = ComputeAuditEntryHash(
                currentSequence,
                entry.Id.ToString(),
                currentPrevHash,
                entry.OccurredAt,
                entry.EventType,
                entry.ActorSid.Value,
                entry.TargetTable,
                entry.TargetColumn,
                entry.Decision,
                entry.TraceId,
                entry.DetailsJson,
                effectiveTenantId);

            entry.PrevHash = currentPrevHash;
            entry.EntryHash = entryHash;

            pId.Value = entry.Id.ToString();
            pOcc.Value = entry.OccurredAt.ToString("O");
            pEvent.Value = (object?)entry.EventType ?? DBNull.Value;
            pActor.Value = (object?)entry.ActorSid.Value ?? DBNull.Value;
            pTarget.Value = (object?)entry.TargetTable ?? DBNull.Value;
            pCol.Value = (object?)entry.TargetColumn ?? DBNull.Value;
            pDec.Value = (object?)entry.Decision ?? DBNull.Value;
            pTrace.Value = (object?)entry.TraceId ?? DBNull.Value;
            pDet.Value = (object?)entry.DetailsJson ?? DBNull.Value;
            pPrev.Value = currentPrevHash;
            pHash.Value = entryHash;
            pTenantId.Value = effectiveTenantId;
            pSeq.Value = currentSequence;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            lastEntryHash = entryHash;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        _lastAuditHash = lastEntryHash;
        Interlocked.Exchange(ref _lastAuditSeq, lastSequence);

        if (_auditAnchorStore != null)
        {
            var anchor = CreateSignedAnchor(lastSequence, lastEntryHash);
            try
            {
                // Monotonic: replicas sharing one anchor store must never move the anchor backwards.
                var current = _auditAnchorStore.Load();
                if (current == null || current.Sequence < anchor.Sequence)
                {
                    _auditAnchorStore.Save(anchor);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to persist external audit chain anchor at seq {Sequence}.", anchor.Sequence);
            }
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        var list = new List<AuditLogEntry>();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var tenantFilter = tenantId != null ? " WHERE tenant_id = @tenantId" : "";
        cmd.CommandText = $@"SELECT TOP (@lim) id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {tenantFilter}
                            ORDER BY rowid DESC";
        cmd.Parameters.Add("@lim", SqlDbType.Int).Value = Math.Max(1, limit);
        if (tenantId != null)
        {
            cmd.Parameters.Add("@tenantId", SqlDbType.NVarChar, 128).Value = tenantId.Value.Value;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
    }

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        var list = new List<AuditLogEntry>();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetTable))
        {
            whereClauses.Add("LOWER(target_table) = LOWER(@targetTable)");
            cmd.Parameters.Add("@targetTable", SqlDbType.NVarChar, 512).Value = targetTable;
        }
        if (actorSid != null)
        {
            whereClauses.Add("actor_sid = @actorSid");
            cmd.Parameters.Add("@actorSid", SqlDbType.NVarChar, 256).Value = actorSid.Value.Value;
        }
        if (since != null)
        {
            whereClauses.Add("occurred_at >= @since");
            cmd.Parameters.Add("@since", SqlDbType.NVarChar, 64).Value = since.Value.ToString("O");
        }
        if (tenantId != null)
        {
            whereClauses.Add("tenant_id = @tenantId");
            cmd.Parameters.Add("@tenantId", SqlDbType.NVarChar, 128).Value = tenantId.Value.Value;
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        cmd.CommandText = $@"SELECT TOP (@lim) id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {whereSql}
                            ORDER BY rowid DESC";
        cmd.Parameters.Add("@lim", SqlDbType.Int).Value = Math.Max(1, limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
    }

    private static AuditLogEntry ReadAuditEntry(SqlDataReader reader)
    {
        return new AuditLogEntry
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
        };
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        if (_auditChainViolation != null)
        {
            return false;
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, seq, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            ORDER BY rowid ASC";

        // Review PG-1: snapshot of what this process knows, taken under the writer lock so hash and seq belong together.
        long knownSeq;
        string knownHash;
        await _auditLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            knownSeq = Interlocked.Read(ref _lastAuditSeq);
            knownHash = _lastAuditHash;
        }
        finally
        {
            _auditLock.Release();
        }

        string? hashAtKnownSeq = null;
        var expectedPrevHash = AuditGenesisHash;
        long position = 0;
        AuditChainAnchor? anchor = TryLoadVerifiedAnchor(out bool anchorInvalid);
        if (anchorInvalid)
        {
            return false;
        }

        bool anchorEntryMatched = false;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            position++;
            var id = reader.GetString(0);
            var occurredAt = DateTimeOffset.Parse(reader.GetString(1));
            var eventType = reader.GetString(2);
            var actorSid = reader.GetString(3);
            var targetTable = reader.GetString(4);
            var targetColumn = reader.IsDBNull(5) ? null : reader.GetString(5);
            var decision = reader.GetString(6);
            var traceId = reader.GetString(7);
            var detailsJson = reader.GetString(8);
            var prevHash = reader.GetString(9);
            var entryHash = reader.GetString(10);
            long? sequence = reader.IsDBNull(11) ? null : reader.GetInt64(11);
            var tenantId = reader.IsDBNull(12) ? TenantId.LegacySingleTenant.Value : reader.GetString(12);

            if (sequence.HasValue && sequence.Value != position)
            {
                FlagAuditChainViolation($"Audit entry sequence gap/divergence: expected seq {position}, got {sequence.Value}.");
                return false;
            }

            if (!FixedTimeEqualsString(prevHash, expectedPrevHash))
            {
                FlagAuditChainViolation($"Broken prev_hash chain at position {position} (id {id}). Expected {expectedPrevHash}, got {prevHash}.");
                return false;
            }

            var computedHash = ComputeAuditEntryHash(
                sequence,
                id,
                prevHash,
                occurredAt,
                eventType,
                actorSid,
                targetTable,
                targetColumn,
                decision,
                traceId,
                detailsJson,
                tenantId);

            if (!FixedTimeEqualsString(entryHash, computedHash))
            {
                FlagAuditChainViolation($"Invalid entry_hash at position {position} (id {id}). Hash does not match row payload.");
                return false;
            }

            if (anchor != null && anchor.Sequence == position)
            {
                anchorEntryMatched = FixedTimeEqualsString(entryHash, anchor.EntryHash);
                if (!anchorEntryMatched)
                {
                    FlagAuditChainViolation($"Anchor mismatch at sequence {position}. DB entry hash does not match signed anchor.");
                    return false;
                }
            }

            if (position == knownSeq)
            {
                hashAtKnownSeq = entryHash;
            }

            expectedPrevHash = entryHash;
        }

        // The anchor may lag behind the chain (other replicas, anchor written after commit); it must never be ahead of it.
        if (anchor != null && anchor.Sequence > 0 && (anchor.Sequence > position || !anchorEntryMatched))
        {
            FlagAuditChainViolation($"External signed anchor claims seq {anchor.Sequence}, but DB chain ended at position {position}.");
            return false;
        }

        // Review PG-1: a longer chain than this process knew is normal (other replicas / concurrent writers); a shorter one,
        // or a different hash at the known position, is a violation.
        if (knownSeq > 0 && (position < knownSeq || !FixedTimeEqualsString(hashAtKnownSeq, knownHash)))
        {
            FlagAuditChainViolation($"Tail divergence: last known (seq {knownSeq}, hash {knownHash}) vs scanned chain (seq {position}, hash at known seq {hashAtKnownSeq ?? "n/a"}).");
            return false;
        }

        if (position > 0)
        {
            await _auditLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (position > Interlocked.Read(ref _lastAuditSeq))
                {
                    _lastAuditHash = expectedPrevHash;
                    Interlocked.Exchange(ref _lastAuditSeq, position);
                }
            }
            finally
            {
                _auditLock.Release();
            }
        }

        return true;
    }

    public async Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT MIN(rowid), MAX(rowid)
                            FROM AUDIT_LOG_ENTRIES
                            WHERE occurred_at >= @from AND occurred_at < @to";
        cmd.Parameters.Add("@from", SqlDbType.NVarChar, 64).Value = windowFrom.ToString("O");
        cmd.Parameters.Add("@to", SqlDbType.NVarChar, 64).Value = windowTo.ToString("O");

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        var firstRowId = reader.GetInt64(0);
        var lastRowId = reader.GetInt64(1);
        await reader.CloseAsync().ConfigureAwait(false);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";
        countCmd.Parameters.Add("@a", SqlDbType.BigInt).Value = firstRowId;
        countCmd.Parameters.Add("@b", SqlDbType.BigInt).Value = lastRowId;
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return new AuditChainRange(firstRowId, lastRowId, count);
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);

        var list = new List<AuditChainRecord>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT TOP (@lim) rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column,
                                   decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            WHERE rowid > @after AND rowid <= @last
                            ORDER BY rowid ASC";
        cmd.Parameters.Add("@after", SqlDbType.BigInt).Value = afterRowId;
        cmd.Parameters.Add("@last", SqlDbType.BigInt).Value = lastRowIdInclusive;
        cmd.Parameters.Add("@lim", SqlDbType.Int).Value = Math.Clamp(pageSize, 1, 10000);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    private (string? Hash, long Sequence) ReadAuditTail(SqlTransaction? tx)
    {
        string? hash = null;
        long? seq = null;

        var conn = tx?.Connection ?? OpenConnection();
        bool ownsConn = tx == null;
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT TOP (1) entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    hash = reader.GetString(0);
                    seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            if (hash == null) return (null, 0);
            if (seq.HasValue) return (hash, seq.Value);

            using var countCmd = conn.CreateCommand();
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";
            var count = Convert.ToInt64(countCmd.ExecuteScalar());
            return (hash, count);
        }
        finally
        {
            if (ownsConn)
            {
                conn.Dispose();
            }
        }
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
            anchor = _auditAnchorStore?.Load();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load audit anchor from external store.");
            invalid = true;
            return null;
        }

        if (anchor == null) return null;

        var expected = ComputeAnchorSignature(anchor.Sequence, anchor.EntryHash, anchor.UpdatedAt);
        if (!FixedTimeEqualsString(expected, anchor.Signature))
        {
            _logger?.LogCritical("External audit chain anchor signature INVALID at sequence {Sequence}.", anchor.Sequence);
            invalid = true;
            return null;
        }

        return anchor;
    }

    private void InitializeAuditChainAnchor()
    {
        if (_auditAnchorStore == null) return;
        if (!_isDevOrTest && _auditAnchorStore is InMemoryAuditChainAnchorStore)
        {
            _logger?.LogWarning("Audit chain anchor is only held in memory (no tamper protection across restarts); configure Audit:ChainAnchorPath on a separate, shared volume or WORM store.");
        }

        var anchor = TryLoadVerifiedAnchor(out bool invalid);
        if (invalid)
        {
            FlagAuditChainViolation("External signed audit anchor signature verification failed at startup.");
            return;
        }

        // Another replica may have written since the schema init read the tail; re-read it after the anchor was loaded.
        var (freshHash, freshSeq) = ReadAuditTail(null);
        if (freshHash != null && freshSeq > Interlocked.Read(ref _lastAuditSeq))
        {
            _lastAuditHash = freshHash;
            Interlocked.Exchange(ref _lastAuditSeq, freshSeq);
        }

        if (anchor == null)
        {
            if (_lastAuditSeq > 0)
            {
                // Review PG-2 (as SQLite): entries without an anchor outside Development indicate deletion of the
                // anchor, unless the store is only held in memory (then there is nothing that could have survived a restart).
                if (!_isDevOrTest && _auditAnchorStore is not InMemoryAuditChainAnchorStore)
                {
                    FlagAuditChainViolation(
                        $"Audit DB has {_lastAuditSeq} entries, but the external audit chain anchor is missing. Potential truncation or unauthorized deletion detected.");
                    return;
                }

                var newAnchor = CreateSignedAnchor(_lastAuditSeq, _lastAuditHash);
                try
                {
                    _auditAnchorStore.Save(newAnchor);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to seed initial external anchor for existing audit chain at seq {Sequence}.", _lastAuditSeq);
                }
            }
            return;
        }

        if (_lastAuditSeq < anchor.Sequence)
        {
            FlagAuditChainViolation(
                $"Audit chain truncated! External verified anchor has sequence {anchor.Sequence}, but database only reaches {_lastAuditSeq}.");
        }
        else if (_lastAuditSeq == anchor.Sequence && !FixedTimeEqualsString(_lastAuditHash, anchor.EntryHash))
        {
            FlagAuditChainViolation(
                $"Audit chain tail mutated! Sequence {anchor.Sequence} matches external anchor, but hash '{_lastAuditHash}' does not match anchored '{anchor.EntryHash}'.");
        }
    }

    private void FlagAuditChainViolation(string reason)
    {
        _auditChainViolation ??= reason;
        _isAuditPipelineFaulted = true;
        _logger?.LogCritical("AUDIT CHAIN INTEGRITY VIOLATION: {Reason}", reason);
    }

    private static string EscapeField(string? v)
    {
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    private static bool FixedTimeEqualsString(string? a, string? b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));
}
