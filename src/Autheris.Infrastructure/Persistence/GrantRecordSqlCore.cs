namespace Autheris.Infrastructure.Persistence;

using System.Data;
using System.Data.Common;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public sealed class GrantRecordSqlCore(GovernanceRepository repository) : IGrantRecordStore
{
    private const string PgLockHeadSql = "INSERT INTO GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES (@tenant_id, 0, @genesis) ON CONFLICT (tenant_id) DO NOTHING; SELECT last_seq, last_hash FROM GRANT_RECORD_HEADS WHERE tenant_id = @tenant_id FOR UPDATE;";
    private const string SqlServerLockHeadSql = "IF NOT EXISTS (SELECT 1 FROM dbo.GRANT_RECORD_HEADS WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant_id) INSERT INTO dbo.GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES (@tenant_id, 0, @genesis); SELECT last_seq, last_hash FROM dbo.GRANT_RECORD_HEADS WITH (UPDLOCK, ROWLOCK) WHERE tenant_id = @tenant_id;";
    private const string InsertRecordSql = "INSERT INTO GRANT_RECORDS (tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id) VALUES (@tenant_id, @seq, @id, @record_type, @consent_id, @request_id, @grant_source, @profile_assignment_id, @grantee_type, @grantee_ref, @object_id, @scope_json, @valid_from, @valid_until, @requested_by, @decision_json, @decided_at, @recorded_at, @prev_hash, @record_hash, @signature, @key_id);";
    private const string AdvanceHeadSql = "UPDATE GRANT_RECORD_HEADS SET last_seq = @seq, last_hash = @record_hash WHERE tenant_id = @tenant_id;";
    private const string UpdateConsentSeqSql = "UPDATE CONSENTS SET grant_record_seq = @seq WHERE tenant_id = @tenant_id AND id = @id;";
    private const string SelectTenantChainSql = "SELECT tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id FROM GRANT_RECORDS WHERE tenant_id = @tenant_id ORDER BY seq ASC;";
    private readonly GovernanceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var cmd = CreateTxCommand(transaction, _repository.Dialect is PostgreSqlGovernanceDialect ? PgLockHeadSql : SqlServerLockHeadSql);
        cmd.AddBound("@tenant_id", tenantId); cmd.AddBound("@genesis", GrantRecordChain.GenesisHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetInt64(0), reader.GetString(1)) : (0L, GrantRecordChain.GenesisHash);
    }

    public async Task AppendRecordAndAdvanceHeadAsync(DbTransaction transaction, GrantRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction); ArgumentNullException.ThrowIfNull(record);
        using (var cmd = CreateTxCommand(transaction, InsertRecordSql)) { BindRecord(cmd, record); await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        using var headCmd = CreateTxCommand(transaction, AdvanceHeadSql);
        headCmd.AddBound("@tenant_id", record.TenantId); headCmd.AddBound("@seq", record.Seq, DbType.Int64); headCmd.AddBound("@record_hash", record.RecordHash);
        await headCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateConsentGrantRecordSeqAsync(DbTransaction transaction, string tenantId, Guid consentId, long seq, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var cmd = CreateTxCommand(transaction, UpdateConsentSeqSql);
        cmd.AddBound("@tenant_id", tenantId); cmd.AddBound("@id", consentId.ToString("D")); cmd.AddBound("@seq", seq, DbType.Int64);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        (cmd.CommandText, var items) = (SelectTenantChainSql, new List<GrantRecord>());
        cmd.AddBound("@tenant_id", tenantId);
        await using var reader = await _repository.ExecuteReaderAsync(cmd, "GrantRecords.ReadTenantChain", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(ReadRecord(reader));
        return items;
    }

    private static DbCommand CreateTxCommand(DbTransaction tx, string sql) { var cmd = (tx.Connection ?? throw new InvalidOperationException("Transaction has no connection.")).CreateCommand(); (cmd.Transaction, cmd.CommandText) = (tx, sql); return cmd; }

    private static void BindRecord(DbCommand cmd, GrantRecord r)
    {
        cmd.AddBound("@tenant_id", r.TenantId); cmd.AddBound("@seq", r.Seq, DbType.Int64); cmd.AddBound("@id", r.Id.ToString("D")); cmd.AddBound("@record_type", GrantRecordCanonicalizer.ToSqlString(r.RecordType));
        cmd.AddBound("@consent_id", r.ConsentId?.ToString("D")); cmd.AddBound("@request_id", r.RequestId?.ToString("D")); cmd.AddBound("@grant_source", r.GrantSource.ToSqlString()); cmd.AddBound("@profile_assignment_id", r.ProfileAssignmentId?.ToString("D"));
        cmd.AddBound("@grantee_type", r.GranteeType); cmd.AddBound("@grantee_ref", r.GranteeRef); cmd.AddBound("@object_id", r.ObjectId); cmd.AddBound("@scope_json", r.ScopeJson);
        cmd.AddBound("@valid_from", r.ValidFrom?.ToUniversalTime()); cmd.AddBound("@valid_until", r.ValidUntil?.ToUniversalTime()); cmd.AddBound("@requested_by", r.RequestedBy); cmd.AddBound("@decision_json", r.DecisionJson);
        cmd.AddBound("@decided_at", r.DecidedAt.ToUniversalTime()); cmd.AddBound("@recorded_at", r.RecordedAt.ToUniversalTime()); cmd.AddBound("@prev_hash", r.PrevHash); cmd.AddBound("@record_hash", r.RecordHash); cmd.AddBound("@signature", r.Signature); cmd.AddBound("@key_id", r.KeyId);
    }

    private static GrantRecord ReadRecord(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), Guid.Parse(reader.GetString(2)), GrantRecordCanonicalizer.ParseSqlString(reader.GetString(3)),
            ConsentWorkflowSql.ReadOptionalGuid(reader, 4), ConsentWorkflowSql.ReadOptionalGuid(reader, 5), ConsentWorkflowSql.ReadGrantSource(reader, 6),
            ConsentWorkflowSql.ReadOptionalGuid(reader, 7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 12), ConsentWorkflowSql.ReadOptionalTimestamp(reader, 13), reader.GetString(14),
            reader.GetString(15), ConsentWorkflowSql.ReadOptionalTimestamp(reader, 16) ?? DateTimeOffset.MinValue, ConsentWorkflowSql.ReadOptionalTimestamp(reader, 17) ?? DateTimeOffset.MinValue,
            reader.GetString(18), reader.GetString(19), reader.GetString(20), reader.GetString(21));
}
