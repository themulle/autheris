using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository : IAuditChainExportSource, IAuditPurgeKeyProvider
{
    byte[]? IAuditPurgeKeyProvider.GetAuditHmacKey() => _auditHmacKey;

    internal static class AuditSql
    {
        public const string SelectLatestPurgeCheckpoint = @"SELECT first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature
                            FROM AUDIT_PURGE_CHECKPOINTS
                            ORDER BY first_surviving_seq DESC
                            LIMIT 1";

        public const string InsertPurgeCheckpoint = @"INSERT INTO AUDIT_PURGE_CHECKPOINTS (id, first_surviving_seq, surviving_prev_hash, purged_count, purged_through_utc, created_at, signature, external_signature)
                            VALUES (@id, @firstSeq, @prevHash, @purgedCount, @purgedThrough, @createdAt, @sig, @extSig)";

        public static readonly AuditPurgePlanStatements PurgePlan = new(
            SelectOldestRowSql: "SELECT seq, occurred_at FROM AUDIT_LOG_ENTRIES ORDER BY seq ASC LIMIT 1",
            SelectSurvivingRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES WHERE occurred_at >= @threshold ORDER BY seq ASC LIMIT 1",
            SelectNewestRowSql: "SELECT seq, prev_hash FROM AUDIT_LOG_ENTRIES ORDER BY seq DESC LIMIT 1",
            SelectMaxEligibleSeqSql: "SELECT MAX(seq) FROM AUDIT_LOG_ENTRIES WHERE seq < @survivingSeq");

        public static string GetAuditLogEntries(bool withTenant) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {(withTenant ? " WHERE tenant_id = @tenantId" : "")}
                            ORDER BY rowid ASC
                            LIMIT @lim";

        public static string FormatQueryAuditLogs(string whereSql) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                            FROM AUDIT_LOG_ENTRIES
                            {whereSql}
                            ORDER BY occurred_at DESC
                            LIMIT @lim";

        public static string FormatQueryAuditLogPage(string wherePredicate) =>
            $@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                            FROM AUDIT_LOG_ENTRIES
                            WHERE {wherePredicate}
                            ORDER BY seq DESC
                            LIMIT @lim";

        public const string SelectChainRangeMinMax = @"SELECT MIN(rowid), MAX(rowid) FROM AUDIT_LOG_ENTRIES
                                    WHERE occurred_at >= @from AND occurred_at <= @to";

        public const string SelectChainRangeCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";

        public const string SelectChainPage = @"SELECT rowid, seq, id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id
                                FROM AUDIT_LOG_ENTRIES
                                WHERE rowid > @after AND rowid <= @last
                                ORDER BY rowid ASC
                                LIMIT @lim";

        public const string SelectTail = "SELECT entry_hash, seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1";

        public const string SelectCount = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES";

        public const string SelectVerifyScan = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column, decision, trace_id, details_json, prev_hash, entry_hash, seq, tenant_id
                                FROM AUDIT_LOG_ENTRIES
                                ORDER BY rowid ASC";
    }
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        StoredTenant.RequireWritable(entry.TenantId); // DT-SC-04: a missing tenant is refused at the door, not dead-lettered later
        // Tier-B events are only enqueued (no gate); they must not be accepted before the audit processor runs.
        ThrowIfNotReady();

        if (entry.Caller == null && _callerContextProvider != null)
        {
            entry.Caller = _callerContextProvider.ResolveCallerContext();
        }

        // Tier-A: Security critical mutations, admin actions, or denied requests are synchronous (Fail-Closed)
        bool isTierA = AuditChainViolationGate.IsTierA(entry);

        // SR-L-08: a latched chain violation is applied per policy to both tiers (Tier-A is marked, Tier-B refused by default).
        var violation = Volatile.Read(ref _auditChainViolation);
        var admitted = AuditChainViolationGate.Admit(entry, isTierA, violation, ViolationPolicy, _logger);

        // SEC R2-3: Audit:SynchronousQueryAudit commits query events before the request is answered (no crash window).
        if (isTierA || _options?.Audit?.SynchronousQueryAudit == true)
        {
            await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
            var conn = scope.Connection;
            {
                await RecordAuditEventInternalAsync(conn, admitted, ct).ConfigureAwait(false);
                if (!ReferenceEquals(admitted, entry))
                {
                    entry.PrevHash = admitted.PrevHash;
                    entry.EntryHash = admitted.EntryHash;
                }

                if (violation != null && isTierA && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
                {
                    await EmitChainFaultEventOnceAsync(conn, admitted, violation, ct).ConfigureAwait(false);
                }
            }
            return;
        }

        if (_isAuditPipelineFaulted)
        {
            _logger?.LogError("Audit pipeline is faulted; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new AuditUnavailableException("Audit pipeline is in a faulted state (fail-closed).");
        }

        // Tier-B: High-volume query reads (TABLE_QUERY / WEBSQL_QUERY ALLOW) are enqueued to the bounded channel with timeout (fail-closed)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _auditChannel.Writer.WriteAsync(entry, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogError("Audit channel saturated; failing closed on Tier-B audit event for table {Table}.", entry.TargetTable);
            throw new AuditUnavailableException("Audit pipeline saturated (fail-closed).");
        }
    }

    public async Task RecordAuditEventsAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        foreach (var candidate in entries ?? throw new ArgumentNullException(nameof(entries)))
        {
            StoredTenant.RequireWritable(candidate.TenantId); // DT-SC-04
        }

        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        ThrowIfNotReady();

        var violation = Volatile.Read(ref _auditChainViolation);
        var admittedBatch = new List<AuditLogEntry>(entries.Count);
        AuditLogEntry? firstTierA = null;

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Caller == null && _callerContextProvider != null)
            {
                entry.Caller = _callerContextProvider.ResolveCallerContext();
            }

            bool isTierA = AuditChainViolationGate.IsTierA(entry);
            var admitted = AuditChainViolationGate.Admit(entry, isTierA, violation, ViolationPolicy, _logger);
            admittedBatch.Add(admitted);
            if (isTierA && firstTierA == null)
            {
                firstTierA = admitted;
            }
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        await RecordAuditEventsBatchInternalAsync(conn, admittedBatch, ct).ConfigureAwait(false);

        for (int i = 0; i < entries.Count; i++)
        {
            if (!ReferenceEquals(admittedBatch[i], entries[i]))
            {
                entries[i].PrevHash = admittedBatch[i].PrevHash;
                entries[i].EntryHash = admittedBatch[i].EntryHash;
                entries[i].ChainSequence = admittedBatch[i].ChainSequence;
            }
        }

        if (violation != null && firstTierA != null && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
        {
            await EmitChainFaultEventOnceAsync(conn, firstTierA, violation, ct).ConfigureAwait(false);
        }
    }

    private AuditChainViolationPolicy ViolationPolicy => AuditChainViolationGate.ResolvePolicy(_options?.Audit, _isDevOrTest);

    private int _auditFaultEventEmitted;

    /// <summary>SR-L-08: writes exactly one <c>AUDIT_PIPELINE_FAULT</c> event per latch. Caller holds the audit write lock.</summary>
    private async Task EmitChainFaultEventOnceAsync(SqliteConnection conn, AuditLogEntry trigger, string violation, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _auditFaultEventEmitted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await RecordAuditEventInternalAsync(conn, AuditChainViolationGate.CreateFaultEvent(trigger, violation, ViolationPolicy), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to write the AUDIT_PIPELINE_FAULT event for the latched audit chain violation.");
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
                await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
                var conn = scope.Connection;
                {
                    await RecordAuditEventsBatchInternalAsync(conn, batch, ct).ConfigureAwait(false);
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
            var conn = scope.Connection;
            {
                await RecordAuditEventsBatchInternalAsync(conn, batch, ct).ConfigureAwait(false);
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
                        if (batch.Count > 0)
                        {
                            break;
                        }
                        var delay = deadline - DateTime.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            try
                            {
                                await Task.Delay(1, _auditCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try
                        {
                            await using var scope = await WriteScopeAsync(_auditCts.Token).ConfigureAwait(false);
                            await RecordAuditEventsBatchInternalAsync(scope.Connection, batch, _auditCts.Token).ConfigureAwait(false);
                            if (_isAuditPipelineFaulted)
                            {
                                _isAuditPipelineFaulted = false;
                                _logger?.LogInformation("AU-05: Audit pipeline recovered successfully. Fault flag cleared.");
                            }
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger?.LogWarning(ex, "Attempt {Attempt} to write audit batch of {Count} entries failed.", attempt, batch.Count);
                            if (attempt < 3)
                            {
                                await Task.Delay(50 * attempt, _auditCts.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                _isAuditPipelineFaulted = true;
                                _logger?.LogCritical(ex, "FATAL: Audit batch of {Count} entries failed permanently after 3 attempts. Setting audit pipeline to faulted (fail-closed). Diverting to dead-letter queue.", batch.Count);
                                await WriteToDeadLetterAsync(batch, ex.Message, _auditCts.Token).ConfigureAwait(false);
                                Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
                            }
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
                try
                {
                    await using var scope = await WriteScopeAsync(CancellationToken.None).ConfigureAwait(false);
                    var conn = scope.Connection;
                    {
                        await RecordAuditEventsBatchInternalAsync(conn, batch, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries during shutdown drain; diverting to dead-letter queue.", batch.Count);
                    await WriteToDeadLetterAsync(batch, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            try
            {
                await using var scope = await WriteScopeAsync(CancellationToken.None).ConfigureAwait(false);
                var conn = scope.Connection;
                {
                    await RecordAuditEventsBatchInternalAsync(conn, batch, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to flush audit batch of {Count} entries during shutdown drain; diverting to dead-letter queue.", batch.Count);
                await WriteToDeadLetterAsync(batch, ex.Message, CancellationToken.None).ConfigureAwait(false);
                Autheris.Domain.Diagnostics.GatewayDiagnostics.AuditDeadLetterCounter.Add(batch.Count);
            }
        }
    }

    /// <summary>Diverts a permanently failed batch to the dead-letter table on its own connection under the write gate; never throws.</summary>
    private async Task WriteToDeadLetterAsync(IReadOnlyList<AuditLogEntry> batch, string errorMessage, CancellationToken ct)
    {
        try
        {
            await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
            await WriteToDeadLetterAsync(scope.Connection, batch, errorMessage, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogCritical(ex, "Failed to open a connection for the AUDIT_DEAD_LETTER table!");
        }
    }

    private async Task WriteToDeadLetterAsync(SqliteConnection conn, IReadOnlyList<AuditLogEntry> batch, string errorMessage, CancellationToken ct)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO AUDIT_DEAD_LETTER (id, batch_json, error_message, failed_at, tenant_id)
                                VALUES (@id, @batch, @err, @failed, @tenant)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@batch", JsonSerializer.Serialize(batch));
            cmd.Parameters.AddWithValue("@err", errorMessage);
            cmd.Parameters.AddWithValue("@failed", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@tenant", batch.FirstOrDefault()?.TenantId.Value ?? TenantId.Platform.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception dlEx)
        {
            _logger?.LogCritical(dlEx, "Failed to write audit batch to AUDIT_DEAD_LETTER table!");
        }
    }

    private const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default)
    {
        if (existingTx == null)
        {
            return RecordAuditEventAsync(entry, ct);
        }
        // An audit write inside a caller-managed transaction is only valid on the connection of the operation that holds the write
        // gate: any other transaction could advance the chain outside the gate and fork it.
        if (existingTx is not SqliteTransaction sqliteTx || !ReferenceEquals(sqliteTx.Connection, Volatile.Read(ref _writeConnection)))
        {
            throw new InvalidOperationException("An audit event can only be recorded in a transaction opened by a governance repository write operation (single write gate).");
        }

        return RecordAuditEventInternalAsync(sqliteTx.Connection!, entry, ct, sqliteTx);
    }

    internal Task RecordAuditEventInternalAsync(SqliteConnection conn, AuditLogEntry entry, CancellationToken ct, SqliteTransaction? existingTx = null) =>
        RecordAuditEventsBatchInternalAsync(conn, [entry], ct, existingTx);

    private sealed class AuditTxPendingState
    {
        public required long PreTxSeq { get; init; }
        public required string PreTxHash { get; init; }
        public required long LastSeq { get; set; }
        public required string LastHash { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<SqliteTransaction, AuditTxPendingState> _pendingAuditTxStates = new();

    internal void OnTransactionCommitted(SqliteTransaction tx)
    {
        Exception? anchorFailure = null;
        if (_pendingAuditTxStates.TryRemove(tx, out var state))
        {
            lock (_pendingAuditTxStates)
            {
                if (state.LastSeq >= _lastAuditSeq)
                {
                    _lastAuditSeq = state.LastSeq;
                    _lastAuditHash = state.LastHash;
                }
            }

            if (Volatile.Read(ref _auditChainViolation) == null)
            {
                try
                {
                    // SR-L-07: never move the anchor backwards (same guard as the batch path and PostgreSQL).
                    SaveAnchorMonotonic(CreateSignedAnchor(state.LastSeq, state.LastHash));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist the external audit chain anchor (seq {Sequence}).", state.LastSeq);
                    anchorFailure = ex;
                }
            }
        }

        // M1: the rows are committed, but the Tier-A mutation (consent grant/revoke, manifest apply) is not acknowledged without its anchor.
        if (anchorFailure != null)
        {
            throw new AuditUnavailableException("The audit chain anchor could not be persisted; the change is not acknowledged (fail-closed).", anchorFailure);
        }
    }

    internal void OnTransactionRolledBack(SqliteTransaction tx)
    {
        if (_pendingAuditTxStates.TryRemove(tx, out var state))
        {
            lock (_pendingAuditTxStates)
            {
                if (state.PreTxSeq <= _lastAuditSeq)
                {
                    _lastAuditSeq = state.PreTxSeq;
                    _lastAuditHash = state.PreTxHash;
                }
            }
        }
    }

    internal void RollbackPendingAuditTransactions()
    {
        if (_pendingAuditTxStates.IsEmpty) return;

        lock (_pendingAuditTxStates)
        {
            foreach (var state in _pendingAuditTxStates.Values)
            {
                if (state.PreTxSeq <= _lastAuditSeq)
                {
                    _lastAuditSeq = state.PreTxSeq;
                    _lastAuditHash = state.PreTxHash;
                }
            }
            _pendingAuditTxStates.Clear();
        }
    }

    private async Task RecordAuditEventsBatchInternalAsync(SqliteConnection conn, IReadOnlyList<AuditLogEntry> batch, CancellationToken ct, SqliteTransaction? existingTx = null)
    {
        var violationAtStart = Volatile.Read(ref _auditChainViolation); // M1: only a violation latched DURING this write blocks the acknowledgement
        if (batch.Count == 0) return;

        SqliteTransaction? ownedTx = null;
        var tx = existingTx;
        if (tx == null)
        {
            ownedTx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            tx = ownedTx;
        }

        Exception? anchorFailure = null;
        try
        {
            // SEC H-17: the DB tail is compared with the in-memory reference instead of being adopted blindly.
            var (dbTailHash, dbTailSeq) = ReadAuditTail(conn, tx);
            var effectiveDbHash = dbTailHash ?? AuditGenesisHash;
            long preTxSeq;
            string preTxHash;
            long lastSequence;
            string lastEntryHash;
            lock (_pendingAuditTxStates)
            {
                if (!FixedTimeEqualsString(effectiveDbHash, _lastAuditHash) || dbTailSeq != _lastAuditSeq)
                {
                    // SEC-CRYPTO-01 / SR15-24: If an uncommitted transaction was rolled back without calling OnTransactionRolledBack,
                    // clean up pending states and revert in-memory pointers to the committed DB tail.
                    if (!_pendingAuditTxStates.IsEmpty && dbTailSeq < _lastAuditSeq)
                    {
                        RollbackPendingAuditTransactions();
                    }

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
                }

                preTxSeq = _lastAuditSeq;
                preTxHash = _lastAuditHash;
                lastSequence = _lastAuditSeq;
                lastEntryHash = _lastAuditHash;
            }

            // WP-PF-04: hashes are computed in memory from the single tail read, then the rows are written with multi-row inserts.
            var sequenced = new List<(AuditLogEntry Entry, long Seq)>(batch.Count);
            foreach (var entry in batch)
            {
                lastSequence++;
                entry.PrevHash = lastEntryHash;
                entry.EntryHash = ComputeAuditEntryHash(
                    lastSequence,
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
                sequenced.Add((entry, lastSequence));
                lastEntryHash = entry.EntryHash;
            }

            for (int offset = 0; offset < sequenced.Count; offset += AuditBatchInsertSql.MaxRowsPerStatement)
            {
                int count = Math.Min(AuditBatchInsertSql.MaxRowsPerStatement, sequenced.Count - offset);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = AuditBatchInsertSql.Build(count);
                for (int k = 0; k < count; k++)
                {
                    var (entry, seq) = sequenced[offset + k];
                    cmd.Parameters.Add($"@id{k}", SqliteType.Text).Value = entry.Id.ToString();
                    cmd.Parameters.Add($"@occ{k}", SqliteType.Text).Value = AuditTimestamp.ToStorage(entry.OccurredAt);
                    cmd.Parameters.Add($"@event{k}", SqliteType.Text).Value = entry.EventType;
                    cmd.Parameters.Add($"@actor{k}", SqliteType.Text).Value = entry.ActorSid.Value;
                    cmd.Parameters.Add($"@target{k}", SqliteType.Text).Value = entry.TargetTable;
                    cmd.Parameters.Add($"@col{k}", SqliteType.Text).Value = (object?)entry.TargetColumn ?? DBNull.Value;
                    cmd.Parameters.Add($"@dec{k}", SqliteType.Text).Value = entry.Decision;
                    cmd.Parameters.Add($"@trace{k}", SqliteType.Text).Value = entry.TraceId;
                    cmd.Parameters.Add($"@det{k}", SqliteType.Text).Value = entry.DetailsJson;
                    cmd.Parameters.Add($"@prev{k}", SqliteType.Text).Value = entry.PrevHash;
                    cmd.Parameters.Add($"@hash{k}", SqliteType.Text).Value = entry.EntryHash;
                    cmd.Parameters.Add($"@tenantId{k}", SqliteType.Text).Value = entry.TenantId.Value;
                    cmd.Parameters.Add($"@seq{k}", SqliteType.Integer).Value = seq;
                }

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (ownedTx != null)
            {
                await ownedTx.CommitAsync(ct).ConfigureAwait(false);

                lock (_pendingAuditTxStates)
                {
                    _lastAuditHash = lastEntryHash;
                    _lastAuditSeq = lastSequence;
                }

                // SEC H-17 / E-11: advance the external signed anchor only AFTER the commit while the write lock is still
                // held. Append-only (WORM) anchor stores cannot take an anchor back, so an anchor that is ahead of the
                // database must never exist; a crash between commit and anchor only leaves the anchor behind the tail,
                // which is harmless. An anchor is never advanced while a violation is flagged (it would otherwise
                // "launder" a truncated chain).
                if (Volatile.Read(ref _auditChainViolation) == null)
                {
                    try
                    {
                        SaveAnchorMonotonic(CreateSignedAnchor(lastSequence, lastEntryHash));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to persist the external audit chain anchor (seq {Sequence}).", lastSequence);
                        anchorFailure = ex;
                    }
                }
            }
            else
            {
                // SEC-CRYPTO-01 / SR15-24: Caller-managed transaction (e.g. Consent approval/creation).
                // Do NOT advance the external signed anchor until the caller explicitly commits the transaction!
                // Append-only (WORM) anchor stores cannot take an anchor back, so writing the anchor before commit
                // causes permanent chain violation if the transaction rolls back.
                lock (_pendingAuditTxStates)
                {
                    if (!_pendingAuditTxStates.TryGetValue(existingTx!, out var pendingState))
                    {
                        _pendingAuditTxStates[existingTx!] = new AuditTxPendingState
                        {
                            PreTxSeq = preTxSeq,
                            PreTxHash = preTxHash,
                            LastSeq = lastSequence,
                            LastHash = lastEntryHash
                        };
                    }
                    else
                    {
                        pendingState.LastSeq = lastSequence;
                        pendingState.LastHash = lastEntryHash;
                    }

                    // Tentatively advance in-memory pointers so consecutive writes within this transaction chain correctly
                    _lastAuditHash = lastEntryHash;
                    _lastAuditSeq = lastSequence;
                }
            }
        }
        catch (Exception ex)
        {
            if (ownedTx != null)
            {
                try
                {
                    await ownedTx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rbEx)
                {
                    _logger?.LogWarning(rbEx, "Failed to rollback audit transaction on SQLite connection.");
                }
            }

            _logger?.LogError(ex, "Failed to record audit events batch; transaction rolled back (R-43).");
            throw;
        }
        finally
        {
            ownedTx?.Dispose();
        }

        // WP-PF-04 / SEC-SC-08: a batch containing a Tier-A event is acknowledged only after its anchor is written. The rows are
        // committed (the chain stays linear and the next batch anchors the tail), but the caller fails closed instead of being acknowledged.
        // Tier-B-only batches (channel processor) never fail on a late anchor; the next batch anchors the tail.
        // M1: a chain violation latched DURING this write means no anchor was written; the Tier-A event is then not acknowledged (a violation latched before admission follows the configured policy: Tier-A is written marked).
        if (anchorFailure == null && violationAtStart == null && Volatile.Read(ref _auditChainViolation) != null && ViolationPolicy != AuditChainViolationPolicy.AlertOnly)
        {
            anchorFailure = new InvalidOperationException("The audit hash chain is latched as violated; no anchor was written.");
        }

        if (anchorFailure != null && AuditChainViolationGate.AnyTierA(batch))
        {
            throw new AuditUnavailableException("The audit chain anchor could not be persisted; the event is not acknowledged (fail-closed).", anchorFailure);
        }
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditLogEntriesAsync(scope.Connection, AuditSql.GetAuditLogEntries(tenantId != null), limit, tenantId, ct).ConfigureAwait(false);
    }

    private static AuditLogEntry ReadAuditEntry(SqliteDataReader reader, int o = 0) =>
        AuditSqlCore.ReadAuditEntry(reader, o);

    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        string? targetTable = null,
        Sid? actorSid = null,
        DateTimeOffset? since = null,
        int limit = 1000,
        TenantId? tenantId = null,
        CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.QueryAuditLogsAsync(scope.Connection, AuditSql.FormatQueryAuditLogs, targetTable, actorSid, since, limit, tenantId, ct).ConfigureAwait(false);
    }

    public async Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.QueryAuditLogPageAsync(scope.Connection, AuditSql.FormatQueryAuditLogPage, query, ct).ConfigureAwait(false);
    }

    public async Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetLatestPurgeCheckpointAsync(scope.Connection, AuditSql.SelectLatestPurgeCheckpoint, ct).ConfigureAwait(false);
    }

    public async Task SavePurgeCheckpointAsync(AuditPurgeCheckpoint checkpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var latest = await GetLatestPurgeCheckpointAsync(ct).ConfigureAwait(false);
        if (latest != null)
        {
            if (checkpoint.FirstSurvivingSequence <= latest.FirstSurvivingSequence || checkpoint.PurgedCount <= latest.PurgedCount)
            {
                throw new InvalidOperationException(
                    $"Purge checkpoint must be strictly monotonic. Existing first sequence: {latest.FirstSurvivingSequence}, new: {checkpoint.FirstSurvivingSequence}.");
            }
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        await AuditSqlCore.SavePurgeCheckpointAsync(scope.Connection, AuditSql.InsertPurgeCheckpoint, checkpoint, ct).ConfigureAwait(false);
    }

    public async Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(DateTimeOffset thresholdUtc, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.PlanPurgePrefixAsync(scope.Connection, AuditSql.PurgePlan, thresholdUtc, ct).ConfigureAwait(false);
    }

    public async Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM AUDIT_LOG_ENTRIES
                            WHERE rowid IN (
                                SELECT rowid FROM AUDIT_LOG_ENTRIES
                                WHERE seq <= @maxSeq
                                ORDER BY seq ASC
                                LIMIT @batchSize
                            )";
        cmd.Parameters.AddWithValue("@maxSeq", maxSequenceInclusive);
        cmd.Parameters.AddWithValue("@batchSize", batchSize);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            if (!_pendingAuditTxStates.IsEmpty)
            {
                var (_, tailSeq) = ReadAuditTail(conn, null);
                if (tailSeq < _lastAuditSeq)
                {
                    RollbackPendingAuditTransactions();
                }
            }

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

            var checkpoint = TryLoadVerifiedPurgeCheckpoint(out var checkpointInvalid);
            if (checkpointInvalid)
            {
                FlagAuditChainViolation("External audit purge checkpoint has an invalid signature or format.");
                return false;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = AuditSql.SelectVerifyScan;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            long knownSeq;
            string knownHash;
            lock (_pendingAuditTxStates)
            {
                knownSeq = _lastAuditSeq;
                knownHash = _lastAuditHash;
            }

            var result = await AuditSqlCore.VerifyAuditRowsAsync(
                reader,
                checkpoint,
                anchor,
                knownSeq,
                knownHash,
                _auditHmacKey,
                FlagAuditChainViolation,
                ct).ConfigureAwait(false);

            return result.Success;
        }
    }

    public async Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditChainRangeAsync(scope.Connection, AuditSql.SelectChainRangeMinMax, AuditSql.SelectChainRangeCount, windowFrom, windowTo, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await AuditSqlCore.GetAuditChainPageAsync(scope.Connection, AuditSql.SelectChainPage, afterRowId, lastRowIdInclusive, pageSize, ct).ConfigureAwait(false);
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    /// <summary>Last entry hash and its (explicit or ordinal) sequence number; hash is null for an empty log.</summary>
    private (string? Hash, long Sequence) ReadAuditTail(SqliteConnection conn, SqliteTransaction? tx) =>
        AuditSqlCore.ReadAuditTail(conn, tx, AuditSql.SelectTail, AuditSql.SelectCount);

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
        string? tenantId) =>
        AuditSqlCore.ComputeAuditEntryHash(
            _auditHmacKey,
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

    /// <summary>
    /// SEC H-17: every audit row is written with a sequence number. Rows without one were not written by the gateway, so they
    /// latch a chain violation at startup.
    /// </summary>
    private void LatchUnsequencedAuditRows(SqliteConnection conn)
    {
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE seq IS NULL";
        var unsequenced = Convert.ToInt64(countCmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (unsequenced > 0)
        {
            FlagAuditChainViolation($"{unsequenced} audit row(s) without a sequence number found. Investigate tampering.");
        }
    }

    private AuditChainAnchor CreateSignedAnchor(long sequence, string entryHash) =>
        AuditSqlCore.CreateSignedAnchor(_auditAnchorKey, sequence, entryHash);

    /// <summary>SR-L-07: saves the anchor only if it is ahead of the verified anchor currently stored (or none/invalid exists).</summary>
    private void SaveAnchorMonotonic(AuditChainAnchor anchor)
    {
        var current = TryLoadVerifiedAnchor(out var anchorInvalid);
        if (anchorInvalid)
        {
            // PERS-18: an invalid stored anchor is never overwritten, and the write path no longer succeeds silently: the chain is
            // latched and the caller (which records the failure) refuses to acknowledge the Tier-A event.
            FlagAuditChainViolation("Stored audit chain anchor is invalid; Tier-A writes are refused (fail-closed).");
            throw new InvalidOperationException("The stored audit chain anchor is invalid.");
        }

        if (current == null || current.Sequence < anchor.Sequence)
        {
            _auditAnchorStore.Save(anchor);
        }
    }

    private AuditChainAnchor? TryLoadVerifiedAnchor(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedAnchor(_auditAnchorStore, _auditAnchorKey, _logger, out invalid);

    private AuditPurgeCheckpoint? TryLoadVerifiedPurgeCheckpoint(out bool invalid) =>
        AuditSqlCore.TryLoadVerifiedPurgeCheckpoint(_auditAnchorStore, _auditHmacKey, _logger, out invalid);

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

            // An anchor is never created while a violation is latched (it would launder the suspicious chain).
            if (Volatile.Read(ref _auditChainViolation) != null)
            {
                return;
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

    private void FlagAuditChainViolation(string reason)
    {
        if (Interlocked.CompareExchange(ref _auditChainViolation, reason, null) == null)
        {
            _logger?.LogCritical("SECURITY ALERT - audit hash chain integrity violation: {Reason}", reason);
        }
    }

    private static bool FixedTimeEqualsString(string? a, string? b) =>
        AuditSqlCore.FixedTimeEqualsString(a, b);

    private static string EscapeField(string? s) => Autheris.Domain.Audit.AuditCanonicalizer.CanonicalizeField(s);
}
