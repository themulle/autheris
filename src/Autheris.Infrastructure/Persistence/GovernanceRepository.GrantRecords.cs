namespace Autheris.Infrastructure.Persistence;

using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using System.Data.Common;
using System.Data;

public partial class GovernanceRepository
{
    public sealed class GrantRecordStore(IGovernanceSqlDialect dialect) : IGrantRecordStore
    {
        private const string PgLockHeadSql = "INSERT INTO GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES (@tenant_id, 0, @genesis) ON CONFLICT (tenant_id) DO NOTHING; SELECT last_seq, last_hash FROM GRANT_RECORD_HEADS WHERE tenant_id = @tenant_id FOR UPDATE;";
        private const string SqlServerLockHeadSql = "IF NOT EXISTS (SELECT 1 FROM dbo.GRANT_RECORD_HEADS WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant_id) INSERT INTO dbo.GRANT_RECORD_HEADS (tenant_id, last_seq, last_hash) VALUES (@tenant_id, 0, @genesis); SELECT last_seq, last_hash FROM dbo.GRANT_RECORD_HEADS WITH (UPDLOCK, ROWLOCK) WHERE tenant_id = @tenant_id;";
        private const string InsertRecordSql = "INSERT INTO GRANT_RECORDS (tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id) VALUES (@tenant_id, @seq, @id, @record_type, @consent_id, @request_id, @grant_source, @profile_assignment_id, @grantee_type, @grantee_ref, @object_id, @scope_json, @valid_from, @valid_until, @requested_by, @decision_json, @decided_at, @recorded_at, @prev_hash, @record_hash, @signature, @key_id);";
        private const string InsertConsentSql = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, valid_from, valid_to, is_revoked, tenant_id, description, grant_source, grant_record_seq, profile_assignment_id, expiry_notice_sent_at, object_pattern, data_source, column_set_mode, workflow_mode, profile_item_no, grant_kind, approver_capacity, self_grant, target_columns)
                                VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @sidNorm, @validFrom, @validTo, 0, @tenantId, @description, @grantSource, @grantRecordSeq, @profileAssignmentId, @expiryNoticeSentAt, @objectPattern, @dataSource, @columnSetMode, @workflowMode, @profileItemNo, @grantKind, @approverCapacity, @selfGrant, @targetColumns)";
        private const string AdvanceHeadSql = "UPDATE GRANT_RECORD_HEADS SET last_seq = @seq, last_hash = @record_hash WHERE tenant_id = @tenant_id;";
        private const string UpdateConsentSeqSql = "UPDATE CONSENTS SET grant_record_seq = @seq WHERE tenant_id = @tenant_id AND id = @id;";
        private const string SelectTenantChainSql = "SELECT tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id FROM GRANT_RECORDS WHERE tenant_id = @tenant_id ORDER BY seq ASC;";
        private const string SelectConsentChainSql = "SELECT tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id FROM GRANT_RECORDS WHERE tenant_id = @tenant_id AND consent_id = @consent_id ORDER BY seq ASC;";
        private const string SelectRecordBySeqSql = "SELECT tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id FROM GRANT_RECORDS WHERE tenant_id = @tenant_id AND seq = @seq;";
        private const string SelectRecordRangeSql = "SELECT tenant_id, seq, id, record_type, consent_id, request_id, grant_source, profile_assignment_id, grantee_type, grantee_ref, object_id, scope_json, valid_from, valid_until, requested_by, decision_json, decided_at, recorded_at, prev_hash, record_hash, signature, key_id FROM GRANT_RECORDS WHERE tenant_id = @tenant_id AND seq >= @from_seq AND (@to_seq IS NULL OR seq <= @to_seq) ORDER BY seq ASC;";
        private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

        public async Task<(long LastSeq, string LastHash)> LockAndReadHeadAsync(DbTransaction transaction, string tenantId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            using var cmd = CreateTxCommand(transaction, _dialect is PostgreSqlGovernanceDialect ? PgLockHeadSql : SqlServerLockHeadSql);
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

        public async Task InsertConsentRowAsync(DbTransaction transaction, Consent consent, long grantRecordSeq, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(consent);
            var tenantIdStr = StoredTenant.RequireWritable(consent.TenantId);
            using var cmd = CreateTxCommand(transaction, InsertConsentSql);
            cmd.AddBound("@id", consent.Id.ToString());
            cmd.AddBound("@tid", consent.IsPatternGrant ? (object)DBNull.Value : consent.TableId.ToString());
            cmd.AddBound("@reqId", consent.ConsentRequestId.HasValue ? consent.ConsentRequestId.Value.ToString() : (object)DBNull.Value);
            cmd.AddBound("@effect", consent.Effect.ToString());
            cmd.AddBound("@granteeType", consent.GranteeType.ToString());
            cmd.AddBound("@granteeSid", consent.GranteeSid.HasValue ? consent.GranteeSid.Value.Value : (object)DBNull.Value);
            cmd.AddBound("@sidNorm", ConsentSubjectFilter.NormalizeParameter(consent.GranteeSid?.Value));
            cmd.AddBound("@validFrom", consent.ValidFrom.ToUniversalTime().ToString("O"));
            cmd.AddBound("@validTo", consent.ValidTo.ToUniversalTime().ToString("O"));
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@description", (object?)consent.Description ?? DBNull.Value);
            ConsentWorkflowSql.BindConsentParameters(
                cmd,
                consent.GrantSource,
                grantRecordSeq,
                consent.ProfileAssignmentId,
                consent.ExpiryNoticeSentAt,
                consent.GrantKind,
                consent.ApproverCapacity,
                consent.SelfGrant,
                consent.TargetColumns);
            ConsentPatternSql.BindPatternParameters(cmd, consent);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (consent.IsPatternGrant)
            {
                await ConsentPatternSql.InsertApprovingOwnersAsync(transaction, consent, ct).ConfigureAwait(false);
            }
        }

        public Task<IReadOnlyList<GrantRecord>> ReadTenantChainAsync(string tenantId, CancellationToken ct = default) =>
            ReadRecordListAsync(SelectTenantChainSql, cmd => cmd.AddBound("@tenant_id", tenantId), ct);

        public Task<IReadOnlyList<GrantRecord>> ReadConsentChainAsync(string tenantId, Guid consentId, CancellationToken ct = default) =>
            ReadRecordListAsync(SelectConsentChainSql, cmd =>
            {
                cmd.AddBound("@tenant_id", tenantId);
                cmd.AddBound("@consent_id", consentId.ToString("D"));
            }, ct);

        public async Task<GrantRecord?> ReadBySeqAsync(string tenantId, long seq, CancellationToken ct = default)
        {
            await using var conn = _dialect.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectRecordBySeqSql;
            cmd.AddBound("@tenant_id", tenantId);
            cmd.AddBound("@seq", seq, DbType.Int64);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRecord(reader) : null;
        }

        public Task<IReadOnlyList<GrantRecord>> ReadRangeAsync(string tenantId, long fromSeq, long? toSeq, CancellationToken ct = default) =>
            ReadRecordListAsync(SelectRecordRangeSql, cmd =>
            {
                cmd.AddBound("@tenant_id", tenantId);
                cmd.AddBound("@from_seq", fromSeq, DbType.Int64);
                cmd.AddBound("@to_seq", toSeq.HasValue ? toSeq.Value : (object)DBNull.Value, DbType.Int64);
            }, ct);

        private async Task<IReadOnlyList<GrantRecord>> ReadRecordListAsync(string sql, Action<DbCommand> bind, CancellationToken ct)
        {
            await using var conn = _dialect.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            var items = new List<GrantRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
}
