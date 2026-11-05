namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>Statements required for planning an audit purge prefix.</summary>
internal readonly record struct AuditPurgePlanStatements(
    string SelectOldestRowSql,
    string SelectSurvivingRowSql,
    string SelectNewestRowSql,
    string SelectMaxEligibleSeqSql);

/// <summary>Result of verifying the hash chain rows.</summary>
internal readonly record struct AuditChainVerificationResult(
    bool Success,
    long Position,
    string FinalHash);

/// <summary>
/// Engine-neutral statement execution, parameter binding, row hydration, and cryptographic verification
/// for audit operations across PostgreSQL, SQL Server, and SQLite (ARCH-MNT-22 Step 2).
/// This class operates over <see cref="DbConnection"/> and <see cref="DbCommand"/>, and never opens,
/// commits, rolls back, or disposes connections or transactions.
/// </summary>
internal static class AuditSqlCore
{
    public const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    public static AuditLogEntry ReadAuditEntry(DbDataReader reader, int o = 0) => new()
    {
        Id = Guid.Parse(reader.GetString(o)),
        OccurredAt = DateTimeOffset.Parse(reader.GetString(o + 1)),
        EventType = reader.GetString(o + 2),
        ActorSid = new Sid(reader.GetString(o + 3)),
        TargetTable = reader.GetString(o + 4),
        TargetColumn = reader.IsDBNull(o + 5) ? null : reader.GetString(o + 5),
        Decision = reader.GetString(o + 6),
        TraceId = reader.GetString(o + 7),
        DetailsJson = reader.GetString(o + 8),
        PrevHash = reader.GetString(o + 9),
        EntryHash = reader.GetString(o + 10),
        TenantId = StoredTenant.ParseAudit(reader.IsDBNull(o + 11) ? null : reader.GetString(o + 11))
    };

    public static AuditPurgeCheckpoint ReadPurgeCheckpoint(DbDataReader reader, int o = 0) => new(
        reader.GetInt64(o),
        reader.GetString(o + 1),
        reader.GetInt64(o + 2),
        DateTimeOffset.Parse(reader.GetString(o + 3), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(o + 4), CultureInfo.InvariantCulture),
        reader.GetString(o + 5),
        reader.IsDBNull(o + 6) ? null : reader.GetString(o + 6));

    public static bool FixedTimeEqualsString(string? a, string? b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? string.Empty), Encoding.UTF8.GetBytes(b ?? string.Empty));

    public static string ComputeAuditEntryHash(
        byte[]? hmacKey,
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
            hmacKey,
            sequence,
            id,
            prevHash,
            occurredAt.ToUniversalTime(),
            eventType,
            actorSid,
            targetTable,
            targetColumn,
            decision,
            traceId,
            detailsJson,
            tenantId);
    }

    public static string ComputeAnchorSignature(byte[] anchorKey, long sequence, string entryHash, DateTimeOffset updatedAt)
    {
        var data = Encoding.UTF8.GetBytes($"anchor-v1|{sequence}|{entryHash}|{updatedAt.ToUniversalTime():O}");
        return Convert.ToHexString(HMACSHA256.HashData(anchorKey, data));
    }

    public static AuditChainAnchor CreateSignedAnchor(byte[] anchorKey, long sequence, string entryHash, TimeProvider? clock = null)
    {
        var updatedAt = (clock ?? TimeProvider.System).GetUtcNow();
        return new AuditChainAnchor(sequence, entryHash, updatedAt, ComputeAnchorSignature(anchorKey, sequence, entryHash, updatedAt));
    }

    public static AuditChainAnchor? TryLoadVerifiedAnchor(IAuditChainAnchorStore? store, byte[] anchorKey, ILogger? logger, out bool invalid)
    {
        invalid = false;
        AuditChainAnchor? anchor;
        try
        {
            anchor = store?.Load();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to load audit anchor from external store.");
            invalid = true;
            return null;
        }

        if (anchor == null) return null;

        if (anchor.Sequence < 0 || string.IsNullOrEmpty(anchor.EntryHash) || string.IsNullOrEmpty(anchor.Signature))
        {
            logger?.LogError("External audit anchor has invalid sequence or hash.");
            invalid = true;
            return null;
        }

        var expected = ComputeAnchorSignature(anchorKey, anchor.Sequence, anchor.EntryHash, anchor.UpdatedAt);
        if (!FixedTimeEqualsString(expected, anchor.Signature))
        {
            logger?.LogCritical("External audit chain anchor signature INVALID at sequence {Sequence}.", anchor.Sequence);
            invalid = true;
            return null;
        }

        return anchor;
    }

    public static AuditPurgeCheckpoint? TryLoadVerifiedPurgeCheckpoint(IAuditChainAnchorStore? store, byte[]? hmacKey, ILogger? logger, out bool invalid)
    {
        invalid = false;
        AuditPurgeCheckpoint? checkpoint;
        try
        {
            checkpoint = store?.LoadPurgeCheckpoint();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to load audit purge checkpoint from external store.");
            invalid = true;
            return null;
        }

        if (checkpoint == null) return null;

        if (checkpoint.FirstSurvivingSequence < 1 || string.IsNullOrEmpty(checkpoint.SurvivingPrevHash) || string.IsNullOrEmpty(checkpoint.Signature) || checkpoint.PurgedCount < 0)
        {
            invalid = true;
            return null;
        }

        if (!AuditChainEngine.VerifyPurgeCheckpointSignature(hmacKey, checkpoint))
        {
            logger?.LogCritical("External audit purge checkpoint signature INVALID at sequence {Sequence}.", checkpoint.FirstSurvivingSequence);
            invalid = true;
            return null;
        }

        return checkpoint;
    }

    public static async Task<AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(
        DbConnection conn,
        string selectLatestSql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectLatestSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadPurgeCheckpoint(reader, 0);
        }

        return null;
    }

    public static async Task SavePurgeCheckpointAsync(
        DbConnection conn,
        string insertSql,
        AuditPurgeCheckpoint checkpoint,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.AddBound("@id", Guid.NewGuid().ToString("N"));
        cmd.AddBound("@firstSeq", checkpoint.FirstSurvivingSequence);
        cmd.AddBound("@prevHash", checkpoint.SurvivingPrevHash);
        cmd.AddBound("@purgedCount", checkpoint.PurgedCount);
        cmd.AddBound("@purgedThrough", checkpoint.PurgedThroughUtc.ToString("O"));
        cmd.AddBound("@createdAt", checkpoint.CreatedAtUtc.ToString("O"));
        cmd.AddBound("@sig", checkpoint.Signature);
        cmd.AddBound("@extSig", (object?)checkpoint.ExternalSignature ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(
        DbConnection conn,
        AuditPurgePlanStatements statements,
        DateTimeOffset thresholdUtc,
        CancellationToken ct)
    {
        var thresholdIso = thresholdUtc.ToUniversalTime().ToString("O");

        // 1. Check oldest row
        await using (var minCmd = conn.CreateCommand())
        {
            minCmd.CommandText = statements.SelectOldestRowSql;
            await using var minReader = await minCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await minReader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            var oldestOccurredAt = minReader.GetString(1);
            if (string.CompareOrdinal(oldestOccurredAt, thresholdIso) >= 0)
            {
                return null;
            }
        }

        // 2. Find first surviving entry (earliest entry with occurred_at >= thresholdIso)
        long survivingSeq;
        string survivingPrevHash;
        await using (var survCmd = conn.CreateCommand())
        {
            survCmd.CommandText = statements.SelectSurvivingRowSql;
            survCmd.AddBound("@threshold", thresholdIso);
            await using var survReader = await survCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await survReader.ReadAsync(ct).ConfigureAwait(false))
            {
                survivingSeq = survReader.GetInt64(0);
                survivingPrevHash = survReader.GetString(1);
            }
            else
            {
                await using var newestCmd = conn.CreateCommand();
                newestCmd.CommandText = statements.SelectNewestRowSql;
                await using var newestReader = await newestCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await newestReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
                survivingSeq = newestReader.GetInt64(0);
                survivingPrevHash = newestReader.GetString(1);
            }
        }

        // 3. Find max eligible sequence (< survivingSeq)
        long maxEligibleSeq;
        await using (var maxCmd = conn.CreateCommand())
        {
            maxCmd.CommandText = statements.SelectMaxEligibleSeqSql;
            maxCmd.AddBound("@survivingSeq", survivingSeq);
            var maxObj = await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (maxObj == null || maxObj is DBNull)
            {
                return null;
            }
            maxEligibleSeq = Convert.ToInt64(maxObj, CultureInfo.InvariantCulture);
        }

        long purgeCount = survivingSeq - 1;
        return (maxEligibleSeq, survivingSeq, survivingPrevHash, purgeCount);
    }

    public static async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(
        DbConnection conn,
        string sql,
        int limit,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var list = new List<AuditLogEntry>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@lim", Math.Max(1, limit));
        if (tenantId != null)
        {
            cmd.AddBound("@tenantId", tenantId.Value.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
    }

    public static async Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(
        DbConnection conn,
        Func<string, string> sqlFormatter,
        string? targetTable,
        Sid? actorSid,
        DateTimeOffset? since,
        int limit,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var list = new List<AuditLogEntry>();
        await using var cmd = conn.CreateCommand();

        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetTable))
        {
            whereClauses.Add("LOWER(target_table) = LOWER(@targetTable)");
            cmd.AddBound("@targetTable", targetTable);
        }
        if (actorSid != null)
        {
            whereClauses.Add("actor_sid = @actorSid");
            cmd.AddBound("@actorSid", actorSid.Value.Value);
        }
        if (since != null)
        {
            whereClauses.Add("occurred_at >= @since");
            cmd.AddBound("@since", AuditTimestamp.ToStorage(since.Value));
        }
        if (tenantId != null)
        {
            whereClauses.Add("tenant_id = @tenantId");
            cmd.AddBound("@tenantId", tenantId.Value.Value);
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";
        cmd.CommandText = sqlFormatter(whereSql);
        cmd.AddBound("@lim", Math.Max(1, limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAuditEntry(reader));
        }

        return list;
    }

    public static async Task<AuditLogPage> QueryAuditLogPageAsync(
        DbConnection conn,
        Func<string, string> sqlFormatter,
        AuditLogPageQuery query,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var size = query.EffectivePageSize;

        await using var cmd = conn.CreateCommand();
        var where = new List<string> { "seq IS NOT NULL" };
        if (query.Tenant is { } tenant)
        {
            where.Add("tenant_id = @tenantId");
            cmd.AddBound("@tenantId", tenant.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetTable))
        {
            where.Add("LOWER(target_table) = LOWER(@targetTable)");
            cmd.AddBound("@targetTable", query.TargetTable);
        }

        if (query.Actor is { } actor && !string.IsNullOrWhiteSpace(actor.Value))
        {
            where.Add("actor_sid = @actorSid");
            cmd.AddBound("@actorSid", actor.Value);
        }

        if (query.Since is { } since)
        {
            where.Add("occurred_at >= @since");
            cmd.AddBound("@since", AuditTimestamp.ToStorage(since));
        }

        if (query.BeforeSequence is { } before)
        {
            where.Add("seq < @before");
            cmd.AddBound("@before", before);
        }

        cmd.CommandText = sqlFormatter(string.Join(" AND ", where));
        cmd.AddBound("@lim", size + 1);

        var items = new List<AuditLogEntry>(size + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entry = ReadAuditEntry(reader);
            entry.ChainSequence = reader.GetInt64(12);
            items.Add(entry);
        }

        return AuditPageSlicer.Slice(items, size);
    }

    public static async Task<AuditChainRange?> GetAuditChainRangeAsync(
        DbConnection conn,
        string selectMinMaxSql,
        string selectCountSql,
        DateTimeOffset windowFrom,
        DateTimeOffset windowTo,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectMinMaxSql;
        cmd.AddBound("@from", AuditTimestamp.ToStorage(windowFrom));
        cmd.AddBound("@to", AuditTimestamp.ToStorage(windowTo));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            return null;
        }

        var firstRowId = reader.GetInt64(0);
        var lastRowId = reader.GetInt64(1);
        await reader.CloseAsync().ConfigureAwait(false);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = selectCountSql;
        countCmd.AddBound("@a", firstRowId);
        countCmd.AddBound("@b", lastRowId);
        var count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        return new AuditChainRange(firstRowId, lastRowId, count);
    }

    public static async Task<IReadOnlyList<AuditChainRecord>> GetAuditChainPageAsync(
        DbConnection conn,
        string sql,
        long afterRowId,
        long lastRowIdInclusive,
        int pageSize,
        CancellationToken ct)
    {
        var list = new List<AuditChainRecord>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@after", afterRowId);
        cmd.AddBound("@last", lastRowIdInclusive);
        cmd.AddBound("@lim", Math.Clamp(pageSize, 1, 10000));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long? seq = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            var entry = ReadAuditEntry(reader, 2);
            list.Add(new AuditChainRecord(reader.GetInt64(0), seq, entry));
        }

        return list;
    }

    public static (string? Hash, long Sequence) ReadAuditTail(
        DbConnection conn,
        DbTransaction? tx,
        string tailSql,
        string countSql)
    {
        string? hash = null;
        long? seq = null;

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = tailSql;
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
        countCmd.CommandText = countSql;
        var count = Convert.ToInt64(countCmd.ExecuteScalar());
        return (hash, count);
    }

    public static async Task<AuditChainVerificationResult> VerifyAuditRowsAsync(
        DbDataReader reader,
        AuditPurgeCheckpoint? checkpoint,
        AuditChainAnchor? anchor,
        long knownSeq,
        string knownHash,
        byte[]? hmacKey,
        Action<string> flagViolation,
        CancellationToken ct)
    {
        var expectedPrevHash = checkpoint != null ? checkpoint.SurvivingPrevHash : AuditGenesisHash;
        long position = checkpoint != null ? checkpoint.FirstSurvivingSequence - 1 : 0;
        string? hashAtKnownSeq = null;
        bool anchorEntryMatched = false;
        bool readAnyRows = false;

        if (anchor != null && anchor.Sequence == position)
        {
            anchorEntryMatched = FixedTimeEqualsString(expectedPrevHash, anchor.EntryHash);
            if (!anchorEntryMatched)
            {
                flagViolation($"Anchor mismatch at sequence {position}. DB entry hash does not match signed anchor.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            readAnyRows = true;
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
            var storedTenant = reader.IsDBNull(12) ? null : reader.GetString(12);

            if (!StoredTenant.IsValidAudit(storedTenant))
            {
                flagViolation("Audit entry carries an invalid stored tenant (STORED_TENANT_INVALID).");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            var tenantId = storedTenant!;

            // SEC H-17: every row carries a gap-free sequence number equal to its ordinal position.
            if (!sequence.HasValue)
            {
                flagViolation($"Unsequenced audit row at position {position} (id {id}). Investigate tampering.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            if (checkpoint != null && sequence.Value < checkpoint.FirstSurvivingSequence)
            {
                flagViolation($"Audit entry found before purge checkpoint sequence: got seq {sequence.Value}, checkpoint first sequence is {checkpoint.FirstSurvivingSequence}.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            if (sequence.Value != position)
            {
                flagViolation($"Audit entry sequence gap/divergence: expected seq {position}, got {sequence.Value}.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            if (!FixedTimeEqualsString(prevHash, expectedPrevHash))
            {
                flagViolation($"Broken prev_hash chain at position {position} (id {id}). Expected {expectedPrevHash}, got {prevHash}.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            var computedHash = ComputeAuditEntryHash(
                hmacKey,
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
                flagViolation($"Invalid entry_hash at position {position} (id {id}). Hash does not match row payload.");
                return new AuditChainVerificationResult(false, position, expectedPrevHash);
            }

            if (anchor != null && anchor.Sequence == position)
            {
                anchorEntryMatched = FixedTimeEqualsString(entryHash, anchor.EntryHash);
                if (!anchorEntryMatched)
                {
                    flagViolation($"Anchor mismatch at sequence {position}. DB entry hash does not match signed anchor.");
                    return new AuditChainVerificationResult(false, position, expectedPrevHash);
                }
            }

            if (position == knownSeq)
            {
                hashAtKnownSeq = entryHash;
            }

            expectedPrevHash = entryHash;
        }

        if (checkpoint != null && (!readAnyRows || position < checkpoint.FirstSurvivingSequence))
        {
            flagViolation($"Audit chain missing surviving rows: checkpoint expected seq {checkpoint.FirstSurvivingSequence} but DB has none.");
            return new AuditChainVerificationResult(false, position, expectedPrevHash);
        }

        if (anchor != null && anchor.Sequence >= 0 && (anchor.Sequence > position || !anchorEntryMatched))
        {
            flagViolation($"External signed anchor claims seq {anchor.Sequence}, but DB chain ended at position {position}.");
            return new AuditChainVerificationResult(false, position, expectedPrevHash);
        }

        if (knownSeq > 0 && knownSeq >= (checkpoint?.FirstSurvivingSequence ?? 1) && (position < knownSeq || !FixedTimeEqualsString(hashAtKnownSeq, knownHash)))
        {
            flagViolation($"Tail divergence: last known (seq {knownSeq}, hash {knownHash}) vs scanned chain (seq {position}, hash at known seq {hashAtKnownSeq ?? "n/a"}).");
            return new AuditChainVerificationResult(false, position, expectedPrevHash);
        }

        return new AuditChainVerificationResult(true, position, expectedPrevHash);
    }
}
