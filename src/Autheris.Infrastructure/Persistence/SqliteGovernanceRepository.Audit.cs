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

public partial class SqliteGovernanceRepository : IAuditChainExportSource
{
    public async Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
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
            cmd.Parameters.AddWithValue("@tenant", batch.FirstOrDefault()?.TenantId.Value ?? "unknown");
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
                    cmd.Parameters.Add($"@occ{k}", SqliteType.Text).Value = entry.OccurredAt.ToString("O");
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
        var conn = scope.Connection;
        {
            var list = new List<AuditLogEntry>();
            using var cmd = conn.CreateCommand();
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var list = new List<AuditLogEntry>();
            using var cmd = conn.CreateCommand();
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
    }

    public async Task<Autheris.Application.Governance.Paging.AuditLogPage> QueryAuditLogPageAsync(
        Autheris.Application.Governance.Paging.AuditLogPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        var size = query.EffectivePageSize;
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        using var cmd = scope.Connection.CreateCommand();
        var sql = new StringBuilder(@"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                             decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                                      FROM AUDIT_LOG_ENTRIES
                                      WHERE seq IS NOT NULL");
        if (query.Tenant is { } tenant)
        {
            sql.Append(" AND tenant_id = @tenantId");
            cmd.Parameters.AddWithValue("@tenantId", tenant.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetTable))
        {
            sql.Append(" AND LOWER(target_table) = LOWER(@targetTable)");
            cmd.Parameters.AddWithValue("@targetTable", query.TargetTable);
        }

        if (query.Actor is { } actor && !string.IsNullOrWhiteSpace(actor.Value))
        {
            sql.Append(" AND actor_sid = @actorSid");
            cmd.Parameters.AddWithValue("@actorSid", actor.Value);
        }

        if (query.Since is { } since)
        {
            sql.Append(" AND occurred_at >= @since");
            cmd.Parameters.AddWithValue("@since", since.ToString("O"));
        }

        if (query.BeforeSequence is { } before)
        {
            sql.Append(" AND seq < @before");
            cmd.Parameters.AddWithValue("@before", before);
        }

        sql.Append(" ORDER BY seq DESC LIMIT @lim");
        cmd.Parameters.AddWithValue("@lim", size + 1);
        cmd.CommandText = sql.ToString();

        var items = new List<AuditLogEntry>(size + 1);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new AuditLogEntry
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
                TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : (TenantId.TryParse(reader.GetString(11), out var tid) ? tid : TenantId.LegacySingleTenant),
                ChainSequence = reader.GetInt64(12)
            });
        }

        return AuditPageSlicer.Slice(items, size);
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

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, occurred_at, event_type, actor_sid, target_table, target_column,
                                       decision, trace_id, details_json, prev_hash, entry_hash, tenant_id, seq
                                FROM AUDIT_LOG_ENTRIES
                                ORDER BY rowid ASC";

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var expectedPrevHash = AuditGenesisHash;
            long position = 0;
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
                    FlagAuditChainViolation($"Broken prev_hash chain at position {position} (id {id}).");
                    return false; // Broken chain!
                }

                // SEC H-17: every row carries a gap-free sequence number equal to its ordinal position.
                if (!seq.HasValue)
                {
                    FlagAuditChainViolation($"Unsequenced audit row at position {position} (id {id}). Investigate tampering.");
                    return false;
                }

                if (seq.Value != position)
                {
                    FlagAuditChainViolation($"Audit entry sequence gap/divergence: expected seq {position}, got {seq.Value}.");
                    return false;
                }

                var parsedOccurredAt = DateTimeOffset.Parse(occurredAt);
                var computedHash = ComputeAuditEntryHash(seq, id, prevHash, parsedOccurredAt, eventType, actorSid, targetTable, targetColumn, decision, traceId, detailsJson, tenantId);

                if (!FixedTimeEqualsString(entryHash, computedHash))
                {
                    FlagAuditChainViolation($"Invalid entry_hash at position {position} (id {id}). Hash does not match row payload.");
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
            long knownSeq;
            string knownHash;
            lock (_pendingAuditTxStates)
            {
                knownSeq = _lastAuditSeq;
                knownHash = _lastAuditHash;
            }
            if (knownHash != AuditGenesisHash &&
                (!FixedTimeEqualsString(expectedPrevHash, knownHash) || position != knownSeq))
            {
                var explainedByAnchor = anchor != null
                                        && position > knownSeq
                                        && anchor.Sequence == position
                                        && FixedTimeEqualsString(anchor.EntryHash, expectedPrevHash);
                if (!explainedByAnchor)
                {
                    FlagAuditChainViolation($"Audit chain tail (seq {position}) does not match the in-memory reference (seq {knownSeq}).");
                    return false; // Tail truncation detected!
                }
            }

            return true;
        }
    }

    public async Task<AuditChainRange?> GetAuditChainRangeAsync(DateTimeOffset windowFrom, DateTimeOffset windowTo, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            long firstRowId;
            long lastRowId;
            using (var cmd = conn.CreateCommand())
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

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE rowid BETWEEN @a AND @b";
            countCmd.Parameters.AddWithValue("@a", firstRowId);
            countCmd.Parameters.AddWithValue("@b", lastRowId);
            var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            return new AuditChainRange(firstRowId, lastRowId, count);
        }
    }

    public async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(long afterRowId, long lastRowIdInclusive, int pageSize, CancellationToken ct = default)
    {
        await FlushAuditChannelAsync(ct).ConfigureAwait(false);
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var list = new List<AuditChainRecord>();
            using var cmd = conn.CreateCommand();
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
    }

    public AuditChainAnchor? GetVerifiedChainAnchor() => TryLoadVerifiedAnchor(out _);

    /// <summary>Last entry hash and its (explicit or ordinal) sequence number; hash is null for an empty log.</summary>
    private (string? Hash, long Sequence) ReadAuditTail(SqliteConnection conn, SqliteTransaction? tx)
    {
        string? hash = null;
        long? seq = null;
        using (var cmd = conn.CreateCommand())
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

        using var countCmd = conn.CreateCommand();
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
        return AuditChainEngine.ComputeEntryHash(
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
    }

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

    /// <summary>SR-L-07: saves the anchor only if it is ahead of the verified anchor currently stored (or none/invalid exists).</summary>
    private void SaveAnchorMonotonic(AuditChainAnchor anchor)
    {
        var current = TryLoadVerifiedAnchor(out var anchorInvalid);
        if (anchorInvalid)
        {
            // An invalid stored anchor is never overwritten by a write path; startup/verification flags it.
            return;
        }

        if (current == null || current.Sequence < anchor.Sequence)
        {
            _auditAnchorStore.Save(anchor);
        }
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
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));

    private static string EscapeField(string? s) => Autheris.Domain.Audit.AuditCanonicalizer.CanonicalizeField(s);
}
