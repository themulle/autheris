using System.Data;
using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

public partial class GovernanceRepository
{
    public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
        ReadAndHydratePatternConsentsAsync(TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), subjectSet: null, atTime, c => !c.IsActive(atTime), ct);

    public Task<IReadOnlyList<Consent>> GetActivePatternConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects, TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var subjectSet = subjects as HashSet<Sid> ?? subjects.ToHashSet();
        var scopedTenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        return subjectSet.Count == 0
            ? Task.FromResult<IReadOnlyList<Consent>>([])
            : ReadAndHydratePatternConsentsAsync(scopedTenant, subjectSet, atTime, c => !c.IsActive(atTime), ct);
    }

    public Task<IReadOnlyList<Consent>> GetPatternConsentsWithPendingDeniesAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
        ReadAndHydratePatternConsentsAsync(TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), subjectSet: null, atTime, c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime), ct);

    public Task<IReadOnlyList<Consent>> GetPatternConsentsForSubjectsWithPendingDeniesAsync(
        IEnumerable<Sid> subjects, TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var subjectSet = subjects as HashSet<Sid> ?? subjects.ToHashSet();
        var scopedTenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        return subjectSet.Count == 0
            ? Task.FromResult<IReadOnlyList<Consent>>([])
            : ReadAndHydratePatternConsentsAsync(scopedTenant, subjectSet, atTime, c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime), ct);
    }

    public Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(DateTimeOffset atTime, CancellationToken ct = default) =>
        ReadAndHydratePatternConsentsAsync(null, subjectSet: null, atTime, c => !c.IsActive(atTime) || c.Effect != ConsentEffect.Allow || c.WorkflowMode != ConsentWorkflowMode.Internal, ct);

    private async Task<IReadOnlyList<Consent>> ReadAndHydratePatternConsentsAsync(
        TenantId? tenantId, HashSet<Sid>? subjectSet, DateTimeOffset atTime, Predicate<Consent> exclude, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var consents = new List<Consent>();
        await ConsentPatternSql.ReadActivePatternConsentsForDomainAsync(
            conn, domain: null, tenantId, atTime, subjectSet, seen, consents, ct).ConfigureAwait(false);
        consents.RemoveAll(exclude);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    /// <summary>Constant parameterized SQL helpers for pattern grants on <c>CONSENTS</c> and <c>CONSENT_APPROVING_OWNERS</c>.</summary>
    internal static class ConsentPatternSql
    {
        private const string InsertApprovingOwnerSql =
            "INSERT INTO CONSENT_APPROVING_OWNERS (tenant_id, consent_id, owner_sid, owner_sid_norm) " +
            "VALUES (@tenantId, @consentId, @ownerSid, @ownerSidNorm)";

        private const string SelectApprovingOwnersSql =
            "SELECT owner_sid FROM CONSENT_APPROVING_OWNERS WHERE consent_id = @consentId";

        private const string SelectPatternColumnsPrefix =
            "SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type, " +
            "c.grantee_sid, c.valid_from, c.valid_to, " +
            "c.is_revoked, c.tenant_id, c.grant_source, c.grant_record_seq, " +
            "c.profile_assignment_id, c.expiry_notice_stage, c.object_pattern, " +
            "c.data_source, c.column_set_mode, c.workflow_mode, c.profile_item_no, " +
            "c.renewal_count, c.revoked_by_sid, c.revoked_at, c.revoke_reason, " +
            "c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns " +
            "FROM CONSENTS c WHERE ";

        private const string SelectPatternByIdSql =
            SelectPatternColumnsPrefix + "c.id = @id AND c.object_pattern IS NOT NULL";

        private const string SelectActivePatternsSql =
            SelectPatternColumnsPrefix + "c.is_revoked = 0 AND c.grant_kind = 'DATA' AND c.object_pattern IS NOT NULL";

        private const string SelectActivePatternsByTenantSql =
            SelectPatternColumnsPrefix + "c.is_revoked = 0 AND c.grant_kind = 'DATA' AND c.object_pattern IS NOT NULL AND c.tenant_id = @tenantId";

        private const string SelectActivePatternsByDomainSql =
            SelectPatternColumnsPrefix + "c.is_revoked = 0 AND c.grant_kind = 'DATA' AND c.object_pattern IS NOT NULL AND LOWER(c.data_source) = LOWER(@domain)";

        private const string SelectActivePatternsByTenantDomainSql =
            SelectPatternColumnsPrefix + "c.is_revoked = 0 AND c.grant_kind = 'DATA' AND c.object_pattern IS NOT NULL AND c.tenant_id = @tenantId AND LOWER(c.data_source) = LOWER(@domain)";

        public static void BindPatternParameters(DbCommand cmd, Consent consent)
        {
            if (consent.IsPatternGrant)
            {
                var spec = ObjectPatternSpec.Parse(consent.ObjectPattern!);
                var dataSource = string.IsNullOrWhiteSpace(consent.DataSource) ? spec.DataSource : consent.DataSource.Trim();
                cmd.AddBound("@objectPattern", spec.NormalizedPattern);
                cmd.AddBound("@dataSource", dataSource);
            }
            else
            {
                cmd.AddBound("@objectPattern", DBNull.Value);
                cmd.AddBound("@dataSource", string.IsNullOrWhiteSpace(consent.DataSource) ? (object)DBNull.Value : consent.DataSource.Trim());
            }

            cmd.AddBound("@columnSetMode", consent.ColumnSetMode.ToSqlString());
            cmd.AddBound("@workflowMode", consent.WorkflowMode.ToSqlString());
            cmd.AddBound("@profileItemNo", consent.ProfileItemNo.HasValue ? consent.ProfileItemNo.Value : DBNull.Value, DbType.Int32);
        }

        public static async Task InsertApprovingOwnersAsync(
            DbTransaction transaction,
            Consent consent,
            CancellationToken ct)
        {
            if (consent.ApprovingOwners.Count == 0)
            {
                return;
            }

            var conn = transaction.Connection
                ?? throw new InvalidOperationException("Transaction is not associated with an open connection.");
            var tenantIdStr = StoredTenant.RequireWritable(consent.TenantId);
            var consentIdStr = consent.Id.ToString();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawOwner in consent.ApprovingOwners)
            {
                if (string.IsNullOrWhiteSpace(rawOwner))
                {
                    continue;
                }

                var ownerSid = rawOwner.Trim();
                var ownerNorm = ownerSid.ToUpperInvariant();
                if (!seen.Add(ownerNorm))
                {
                    continue;
                }

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = InsertApprovingOwnerSql;
                cmd.AddBound("@tenantId", tenantIdStr);
                cmd.AddBound("@consentId", consentIdStr);
                cmd.AddBound("@ownerSid", ownerSid);
                cmd.AddBound("@ownerSidNorm", ownerNorm);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        public static async Task<Consent?> ReadPatternConsentByIdRowAsync(
            DbConnection conn,
            Guid consentId,
            CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectPatternByIdSql;
            cmd.AddBound("@id", consentId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            return MapPatternConsentRow(reader);
        }

        public static async Task ReadActivePatternConsentsForDomainAsync(
            DbConnection conn,
            string? domain,
            TenantId? tenantId,
            DateTimeOffset atTime,
            HashSet<Sid>? subjectSet,
            HashSet<Guid> seen,
            List<Consent> consents,
            CancellationToken ct,
            bool includeFuture = false,
            Guid? afterId = null,
            bool allowOnly = false)
        {
            var baseSql = (tenantId != null, !string.IsNullOrWhiteSpace(domain)) switch
            {
                (true, true) => SelectActivePatternsByTenantDomainSql,
                (true, false) => SelectActivePatternsByTenantSql,
                (false, true) => SelectActivePatternsByDomainSql,
                (false, false) => SelectActivePatternsSql
            };

            if (subjectSet == null)
            {
                await ReadActivePatternConsentsChunkAsync(conn, baseSql, domain, tenantId, atTime, subjectSet: null, subjectChunk: null, seen, consents, includeFuture, afterId, allowOnly, ct).ConfigureAwait(false);
                return;
            }

            var normalized = ConsentSubjectFilter.DistinctNormalized(subjectSet);
            if (normalized.Length == 0)
            {
                return;
            }

            foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
            {
                var sql = baseSql + ConsentSubjectFilter.InListPredicate(chunk.Count);
                await ReadActivePatternConsentsChunkAsync(conn, sql, domain, tenantId, atTime, subjectSet, chunk, seen, consents, includeFuture, afterId, allowOnly, ct).ConfigureAwait(false);
            }
        }

        private static async Task ReadActivePatternConsentsChunkAsync(
            DbConnection conn,
            string sql,
            string? domain,
            TenantId? tenantId,
            DateTimeOffset atTime,
            HashSet<Sid>? subjectSet,
            ArraySegment<string>? subjectChunk,
            HashSet<Guid> seen,
            List<Consent> consents,
            bool includeFuture,
            Guid? afterId,
            bool allowOnly,
            CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (tenantId != null)
            {
                cmd.AddBound("@tenantId", tenantId.Value.Value);
            }

            if (!string.IsNullOrWhiteSpace(domain))
            {
                cmd.AddBound("@domain", domain.Trim());
            }

            if (subjectChunk is { Count: > 0 } chunk)
            {
                for (var si = 0; si < chunk.Count; si++)
                {
                    cmd.AddBound("@s" + si, chunk[si]);
                }
            }

            var afterStr = afterId?.ToString("D");
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var validFrom = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
                var validTo = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
                if (atTime >= validTo)
                {
                    continue;
                }

                var effect = Enum.Parse<ConsentEffect>(reader.GetString(3), ignoreCase: true);
                if (!includeFuture && atTime < validFrom && effect != ConsentEffect.Deny)
                {
                    continue;
                }

                if (allowOnly && effect != ConsentEffect.Allow)
                {
                    continue;
                }

                var granteeType = GranteeTypeParsing.ParseStored(reader.GetString(4));
                var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                if (subjectSet != null && (granteeSidStr == null || !subjectSet.Contains(new Sid(granteeSidStr))))
                {
                    continue;
                }

                var id = Guid.Parse(reader.GetString(0));
                if (afterStr is not null && string.Compare(id.ToString("D"), afterStr, StringComparison.Ordinal) <= 0)
                {
                    continue;
                }

                if (!seen.Add(id))
                {
                    continue;
                }

                consents.Add(MapPatternConsentRow(reader));
            }
        }

        public static async Task HydrateApprovingOwnersBatchAsync(
            DbConnection conn,
            List<Consent> consents,
            CancellationToken ct)
        {
            foreach (var consent in consents)
            {
                if (!consent.IsPatternGrant)
                {
                    continue;
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = SelectApprovingOwnersSql;
                cmd.AddBound("@consentId", consent.Id.ToString());

                var owners = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        owners.Add(reader.GetString(0));
                    }
                }

                consent.ApprovingOwners = owners;
            }
        }

        private static Consent MapPatternConsentRow(DbDataReader reader)
        {
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            var dataSource = ConsentWorkflowSql.ReadOptionalString(reader, 15);
            var columnSetRaw = ConsentWorkflowSql.ReadOptionalString(reader, 16);
            var profileItemNo = reader.IsDBNull(18)
                ? (int?)null
                : Convert.ToInt32(reader.GetValue(18), CultureInfo.InvariantCulture);

            return new Consent
            {
                Id = Guid.Parse(reader.GetString(0)),
                TableId = reader.IsDBNull(1) ? Guid.Empty : Guid.Parse(reader.GetString(1)),
                TableIdentifier = new TableIdentifier(dataSource ?? string.Empty, string.Empty, string.Empty),
                ConsentRequestId = ConsentWorkflowSql.ReadOptionalGuid(reader, 2),
                Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), ignoreCase: true),
                GranteeType = GranteeTypeParsing.ParseStored(reader.GetString(4)),
                GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                ValidFrom = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                ValidTo = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                IsRevoked = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
                TenantId = StoredTenant.Parse(ConsentWorkflowSql.ReadOptionalString(reader, 9)),
                GrantSource = ConsentWorkflowSql.ReadGrantSource(reader, 10),
                GrantRecordSeq = ConsentWorkflowSql.ReadInt64OrZero(reader, 11),
                ProfileAssignmentId = ConsentWorkflowSql.ReadOptionalGuid(reader, 12),
                ExpiryNoticeStage = (int)ConsentWorkflowSql.ReadInt64OrZero(reader, 13),
                ObjectPattern = ConsentWorkflowSql.ReadOptionalString(reader, 14),
                DataSource = dataSource,
                ColumnSetMode = ColumnSetModeExtensions.ParseColumnSetModeOrDefault(columnSetRaw),
                WorkflowMode = ConsentWorkflowSql.ReadWorkflowMode(reader, 17),
                ProfileItemNo = profileItemNo,
                RenewalCount = reader.IsDBNull(19) ? 0 : Convert.ToInt32(reader.GetValue(19), CultureInfo.InvariantCulture),
                RevokedBySid = reader.IsDBNull(20) ? (Sid?)null : new Sid(reader.GetString(20)),
                RevokedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 21),
                RevokeReason = ConsentWorkflowSql.ReadOptionalString(reader, 22),
                GrantKind = ConsentWorkflowSql.ReadGrantKind(reader, 23),
                ApproverCapacity = ConsentWorkflowSql.ReadApproverCapacity(reader, 24),
                SelfGrant = ConsentWorkflowSql.ReadBooleanOrFalse(reader, 25),
                TargetColumns = ConsentWorkflowSql.ReadTargetColumns(reader, 26)
            };
        }
    }
}
