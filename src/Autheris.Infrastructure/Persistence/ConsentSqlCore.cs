namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

/// <summary>
/// Engine-neutral statement execution and row mapping for consent queries, batch hydration, consent insertion, consent requests,
/// four-eyes approval state reads, guarded status transitions, and ITSM activation (ARCH-MNT-22 Step 4).
/// This class receives an open connection (and optional transaction) and never opens, commits, rolls back, or disposes either.
/// </summary>
internal static class ConsentSqlCore
{
    public const int ConsentHydrateBatchSize = 500;

    public const string SelectColumnRulesPrefix = "SELECT id, consent_id, table_column_id, column_name, access_level, applicable_scopes FROM CONSENT_COLUMN_RULES WHERE consent_id IN (";

    public const string SelectRowFiltersPrefix = @"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                   operator, value_type, value_json, value_source, user_attribute,
                                   filter_type, dependent_table, dependent_table_alias, foreign_key_column,
                                   primary_key_column, subquery_predicate_json, target_temporal_column,
                                   dependent_valid_from_column, dependent_valid_to_column, target_table_alias,
                                   additional_hops_json, key_set_strategy, key_set_duration_seconds, key_set_max_keys
                            FROM CONSENT_ROW_FILTERS WHERE consent_id IN (";

    public const string InsertColumnRuleSql = "INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level, applicable_scopes) VALUES (@id, @consentId, @colId, @colName, @level, @scopes)";

    public const string InsertRowFilterSql = @"INSERT INTO CONSENT_ROW_FILTERS (
                                        id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                                        filter_type, dependent_table, dependent_table_alias, foreign_key_column, primary_key_column,
                                        subquery_predicate_json, target_temporal_column, dependent_valid_from_column, dependent_valid_to_column,
                                        target_table_alias, additional_hops_json, key_set_strategy, key_set_duration_seconds, key_set_max_keys
                                    ) VALUES (
                                        @id, @consentId, @fg, @colId, @colName, @op, @vType, @vJson, @vSource, @uAttr,
                                        @ft, @dt, @dta, @fk, @pk, @sqp, @ttc, @dvfc, @dvtc, @tta, @hops, @ksStrategy, @ksDuration, @ksMax)";

    public const string InsertConsentRequestSql = @"INSERT INTO CONSENT_REQUESTS (
                                    id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref,
                                    business_justification, status, requested_at, requested_valid_to,
                                    itsm_ticket_id, tenant_id, requester_identifiers_json, context_json
                                ) VALUES (@id, @tid, @sid, @gType, @gRef, @just, @status, @reqAt, @reqTo, @ticket, @tenantId, @reqIds, @ctx)";

    public const string SelectApprovalStepsSql = "SELECT step_number, approver_sid, decision, rejection_reason, decided_at FROM APPROVAL_STEPS WHERE consent_request_id = @id ORDER BY step_number ASC";

    public const string InsertApprovedStepSql = "INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, decided_at) VALUES (@id, @reqId, @step, @sid, 'APPROVED', @now)";

    public const string InsertRejectedStepSql = "INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, rejection_reason, decided_at) VALUES (@id, @reqId, (SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @reqId), @sid, 'REJECTED', @reason, @now)";

    public const string SelectColumnSnapshotSql = @"SELECT c.id, c.column_name, c.is_sensitive, MAX(CASE WHEN m.id IS NOT NULL THEN 1 ELSE 0 END) AS has_mask
                                       FROM TABLE_COLUMNS c LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                       WHERE c.table_id = @tid GROUP BY c.id, c.column_name, c.is_sensitive";

    public static void ReadSubjectConsentRows(DbDataReader reader, TableIdentifier table, DateTimeOffset atTime, HashSet<string> subjectSet, HashSet<Guid> seen, List<Consent> consents)
    {
        while (reader.Read())
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture);
            if (atTime >= validTo) continue;

            var gEffectStr = reader.GetString(3);
            var isDeny = Enum.TryParse<ConsentEffect>(gEffectStr, true, out var parsedEffect) && parsedEffect == ConsentEffect.Deny;
            if (atTime < validFrom && !isDeny) continue;

            var granteeType = Enum.Parse<GranteeType>(reader.GetString(4), true);
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (granteeType != GranteeType.Role && (granteeSidStr == null || !subjectSet.Contains(granteeSidStr))) continue;

            var id = Guid.Parse(reader.GetString(0));
            if (!seen.Add(id)) continue;

            consents.Add(new Consent
            {
                Id = id,
                TableId = Guid.Parse(reader.GetString(1)),
                TableIdentifier = table,
                ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Effect = Enum.Parse<ConsentEffect>(gEffectStr, true),
                GranteeType = granteeType,
                GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(14) ? null : reader.GetString(14))
            });
        }
    }

    public static void ReadAllSubjectConsentRows(DbDataReader reader, DateTimeOffset effectiveAt, HashSet<string> subjectSet, HashSet<string> roleSet, HashSet<Guid> seen, List<Consent> consents)
    {
        while (reader.Read())
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture);
            if (effectiveAt < validFrom || effectiveAt >= validTo) continue;

            var granteeType = Enum.Parse<GranteeType>(reader.GetString(4), true);
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (granteeType == GranteeType.Role)
            {
                var roleName = reader.IsDBNull(7) ? null : reader.GetString(7);
                if (roleName == null || !roleSet.Contains(roleName)) continue;
            }
            else if (granteeSidStr == null || !subjectSet.Contains(granteeSidStr))
            {
                continue;
            }

            var id = Guid.Parse(reader.GetString(0));
            if (!seen.Add(id)) continue;

            consents.Add(new Consent
            {
                Id = id,
                TableId = Guid.Parse(reader.GetString(1)),
                TableIdentifier = new TableIdentifier(reader.GetString(11), reader.GetString(12), reader.GetString(13)),
                ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeType = granteeType,
                GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(14) ? null : reader.GetString(14))
            });
        }
    }

    public static async Task<List<Consent>> ReadActiveConsentsByRequestIdAsync(DbConnection conn, Guid consentRequestId, DateTimeOffset atTime, bool includeNotYetValid, CancellationToken ct)
    {
        var consents = new List<Consent>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.consent_request_id = @reqId";
        cmd.AddBound("@reqId", consentRequestId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture);
            if (atTime >= validTo || (!includeNotYetValid && atTime < validFrom)) continue;

            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            consents.Add(new Consent
            {
                Id = Guid.Parse(reader.GetString(0)),
                TableId = Guid.Parse(reader.GetString(1)),
                TableIdentifier = new TableIdentifier(reader.GetString(11), reader.GetString(12), reader.GetString(13)),
                ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), true),
                GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(14) ? null : reader.GetString(14))
            });
        }

        return consents;
    }

    public static async Task<Consent?> ReadConsentByIdRowAsync(DbConnection conn, Guid consentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                   t.source_name, t.schema_name, t.table_name, c.tenant_id, c.renewal_count
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.id = @id";
        cmd.AddBound("@id", consentId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new Consent
        {
            Id = Guid.Parse(reader.GetString(0)),
            TableId = Guid.Parse(reader.GetString(1)),
            TableIdentifier = new TableIdentifier(reader.GetString(14), reader.GetString(15), reader.GetString(16)),
            ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
            GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), true),
            GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
            RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
            ValidFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            ValidTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            IsRevoked = reader.GetInt32(10) == 1,
            RevokedBySid = reader.IsDBNull(11) ? (Sid?)null : new Sid(reader.GetString(11)),
            RevokedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            RevokeReason = reader.IsDBNull(13) ? null : reader.GetString(13),
            TenantId = StoredTenant.Parse(reader.IsDBNull(17) ? null : reader.GetString(17)),
            RenewalCount = reader.GetInt32(18)
        };
    }

    public static async Task<List<Consent>> ReadExpiringConsentRowsAsync(DbConnection conn, string sql, DateTimeOffset now, DateTimeOffset threshold, int maxResults, CancellationToken ct)
    {
        var consents = new List<Consent>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@now", now.ToString("O"));
        cmd.AddBound("@threshold", threshold.ToString("O"));
        cmd.AddBound("@maxResults", Math.Max(1, maxResults));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            consents.Add(new Consent
            {
                Id = Guid.Parse(reader.GetString(0)),
                TableId = Guid.Parse(reader.GetString(1)),
                TableIdentifier = new TableIdentifier(reader.GetString(11), reader.GetString(12), reader.GetString(13)),
                ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), true),
                GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValidFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                ValidTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(14) ? null : reader.GetString(14)),
                RenewalCount = reader.GetInt32(15)
            });
        }

        return consents;
    }

    public static string BindIdInClause(DbCommand cmd, string prefix, IReadOnlyList<string> values)
    {
        var names = new string[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            names[i] = "@" + prefix + i;
            cmd.AddBound(names[i], values[i]);
        }

        return string.Join(",", names);
    }

    public static async Task LoadConsentDescriptionsAsync(DbConnection conn, IReadOnlyCollection<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;
        var byId = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in consents)
        {
            var key = c.Id.ToString();
            if (!byId.TryGetValue(key, out var list)) byId[key] = list = [];
            list.Add(c);
        }

        var ids = byId.Keys.ToList();
        for (int offset = 0; offset < ids.Count; offset += ConsentHydrateBatchSize)
        {
            var batch = ids.GetRange(offset, Math.Min(ConsentHydrateBatchSize, ids.Count - offset));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, description FROM CONSENTS WHERE description IS NOT NULL AND id IN (" + BindIdInClause(cmd, "did", batch) + ")";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!byId.TryGetValue(reader.GetString(0), out var targets)) continue;
                var text = reader.GetString(1);
                foreach (var c in targets) c.Description = text;
            }
        }
    }

    public static async Task HydrateConsentsBatchAsync(DbConnection conn, List<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;

        var consentIds = consents.Select(c => c.Id.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rulesMap = consents.GroupBy(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new List<ConsentColumnRule>(), StringComparer.OrdinalIgnoreCase);
        var filtersMap = consents.GroupBy(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new List<ConsentRowFilter>(), StringComparer.OrdinalIgnoreCase);

        for (int offset = 0; offset < consentIds.Count; offset += ConsentHydrateBatchSize)
        {
            var batch = consentIds.GetRange(offset, Math.Min(ConsentHydrateBatchSize, consentIds.Count - offset));
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumnRulesPrefix + BindIdInClause(cmd, "cid", batch) + ")";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var cId = reader.GetString(1);
                    if (rulesMap.TryGetValue(cId, out var ruleList)) ruleList.Add(ReadColumnRule(reader, cId));
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectRowFiltersPrefix + BindIdInClause(cmd, "cid", batch) + ")";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var cId = reader.GetString(1);
                    if (filtersMap.TryGetValue(cId, out var filterList)) filterList.Add(ReadRowFilter(reader, cId));
                }
            }
        }

        foreach (var c in consents)
        {
            var key = c.Id.ToString();
            c.ColumnRules = rulesMap[key];
            c.RowFilters = filtersMap[key];
        }
    }

    public static ConsentColumnRule ReadColumnRule(DbDataReader reader, string consentIdStr)
    {
        var rawLevel = reader.GetInt32(4);
        var scopesRaw = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : null;
        var scopes = string.IsNullOrWhiteSpace(scopesRaw)
            ? Array.Empty<string>()
            : scopesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ConsentColumnRule
        {
            Id = Guid.Parse(reader.GetString(0)),
            ConsentId = Guid.Parse(consentIdStr),
            TableColumnId = Guid.Parse(reader.GetString(2)),
            ColumnName = reader.GetString(3),
            AccessLevel = Enum.IsDefined((ColumnAccessLevel)rawLevel) ? (ColumnAccessLevel)rawLevel : ColumnAccessLevel.Deny,
            ApplicableScopes = scopes
        };
    }

    public static ConsentRowFilter ReadRowFilter(DbDataReader reader, string consentIdStr)
    {
        var ftInt = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
        TableIdentifier? depTable = !reader.IsDBNull(11) && TableIdentifier.TryParse(reader.GetString(11), out var dt) ? dt : null;

        IReadOnlyList<SubqueryJoinHop>? hops = null;
        var hopsJson = reader.IsDBNull(20) ? null : reader.GetString(20);
        if (!string.IsNullOrWhiteSpace(hopsJson))
        {
            try { hops = JsonSerializer.Deserialize<List<SubqueryJoinHop>>(hopsJson); }
            catch (JsonException ex) { throw new InvalidOperationException("Malformed additional hops JSON detected in consent row filter.", ex); }
        }

        return new ConsentRowFilter
        {
            Id = Guid.Parse(reader.GetString(0)),
            ConsentId = Guid.Parse(consentIdStr),
            FilterGroup = reader.GetInt32(2),
            TableColumnId = Guid.Parse(reader.GetString(3)),
            ColumnName = reader.GetString(4),
            Operator = reader.GetString(5),
            ValueType = reader.GetString(6),
            ValueJson = reader.GetString(7),
            ValueSource = reader.GetString(8),
            UserAttribute = reader.IsDBNull(9) ? null : reader.GetString(9),
            FilterType = (RowFilterType)ftInt,
            DependentTable = depTable,
            DependentTableAlias = reader.IsDBNull(12) ? null : reader.GetString(12),
            ForeignKeyColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
            PrimaryKeyColumn = reader.IsDBNull(14) ? null : reader.GetString(14),
            SubqueryFilterPredicateJson = reader.IsDBNull(15) ? null : reader.GetString(15),
            TargetTemporalColumn = reader.IsDBNull(16) ? null : reader.GetString(16),
            DependentValidFromColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
            DependentValidToColumn = reader.IsDBNull(18) ? null : reader.GetString(18),
            TargetTableAlias = reader.IsDBNull(19) ? null : reader.GetString(19),
            AdditionalHops = hops,
            KeySetStrategy = reader.IsDBNull(21) ? null : (RowFilterEvaluationStrategy)reader.GetInt32(21),
            KeySetDurationSeconds = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            KeySetMaxKeys = reader.IsDBNull(23) ? null : reader.GetInt32(23)
        };
    }

    public static async Task<Guid> ResolveTableIdOrThrowAsync(DbConnection conn, DbTransaction? tx, string? findTableIdSql, Guid existingTableId, TableIdentifier table, string operationLabel, CancellationToken ct)
    {
        if (existingTableId != Guid.Empty || findTableIdSql == null) return existingTableId;

        await using var findCmd = conn.CreateCommand();
        findCmd.Transaction = tx;
        findCmd.CommandText = findTableIdSql;
        findCmd.AddBound("@domain", table.Domain);
        findCmd.AddBound("@schema", table.Schema);
        findCmd.AddBound("@table", table.TableName);
        var idObj = await findCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (idObj == null) throw new InvalidOperationException($"Cannot create {operationLabel}: Target table '{table}' does not exist in catalog.");

        return Guid.Parse(idObj.ToString()!);
    }

    public static async Task<(Consent Created, AuditLogEntry AuditEntry)> InsertConsentRowsAsync(DbConnection conn, DbTransaction tx, string insertConsentSql, string? findTableIdSql, Consent consent, CancellationToken ct)
    {
        var consentId = consent.Id == Guid.Empty ? Guid.NewGuid() : consent.Id;
        var tableId = await ResolveTableIdOrThrowAsync(conn, tx, findTableIdSql, consent.TableId, consent.TableIdentifier, "consent", ct).ConfigureAwait(false);
        var tenantIdStr = StoredTenant.RequireWritable(consent.TenantId);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = insertConsentSql;
            cmd.AddBound("@id", consentId.ToString());
            cmd.AddBound("@tid", tableId.ToString());
            cmd.AddBound("@reqId", (object?)consent.ConsentRequestId?.ToString() ?? DBNull.Value);
            cmd.AddBound("@effect", consent.Effect.ToString());
            cmd.AddBound("@granteeType", consent.GranteeType.ToString());
            cmd.AddBound("@granteeSid", (object?)consent.GranteeSid?.Value ?? DBNull.Value);
            cmd.AddBound("@sidNorm", ConsentSubjectFilter.NormalizeParameter((object?)consent.GranteeSid?.Value ?? DBNull.Value));
            cmd.AddBound("@roleId", (object?)consent.RoleId?.ToString() ?? DBNull.Value);
            cmd.AddBound("@roleName", (object?)consent.RoleName ?? DBNull.Value);
            cmd.AddBound("@validFrom", consent.ValidFrom.ToUniversalTime().ToString("O"));
            cmd.AddBound("@validTo", consent.ValidTo.ToUniversalTime().ToString("O"));
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@description", (object?)consent.Description ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rule in consent.ColumnRules)
        {
            var ruleId = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
            await using var ruleCmd = conn.CreateCommand();
            ruleCmd.Transaction = tx;
            ruleCmd.CommandText = InsertColumnRuleSql;
            ruleCmd.AddBound("@id", ruleId.ToString());
            ruleCmd.AddBound("@consentId", consentId.ToString());
            ruleCmd.AddBound("@colId", rule.TableColumnId.ToString());
            ruleCmd.AddBound("@colName", rule.ColumnName);
            ruleCmd.AddBound("@level", (int)rule.AccessLevel);
            ruleCmd.AddBound("@scopes", rule.ApplicableScopes != null && rule.ApplicableScopes.Count > 0 ? string.Join(",", rule.ApplicableScopes) : string.Empty);
            await ruleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var filter in consent.RowFilters)
        {
            var filterId = filter.Id == Guid.Empty ? Guid.NewGuid() : filter.Id;
            var hopsJson = filter.AdditionalHops != null && filter.AdditionalHops.Count > 0 ? JsonSerializer.Serialize(filter.AdditionalHops) : null;

            await using var filterCmd = conn.CreateCommand();
            filterCmd.Transaction = tx;
            filterCmd.CommandText = InsertRowFilterSql;
            filterCmd.AddBound("@id", filterId.ToString());
            filterCmd.AddBound("@consentId", consentId.ToString());
            filterCmd.AddBound("@fg", filter.FilterGroup);
            filterCmd.AddBound("@colId", filter.TableColumnId.ToString());
            filterCmd.AddBound("@colName", filter.ColumnName);
            filterCmd.AddBound("@op", filter.Operator);
            filterCmd.AddBound("@vType", filter.ValueType);
            filterCmd.AddBound("@vJson", filter.ValueJson);
            filterCmd.AddBound("@vSource", filter.ValueSource);
            filterCmd.AddBound("@uAttr", (object?)filter.UserAttribute ?? DBNull.Value);
            filterCmd.AddBound("@ft", (int)filter.FilterType);
            filterCmd.AddBound("@dt", filter.DependentTable.HasValue ? (object)filter.DependentTable.Value.ToString() : DBNull.Value);
            filterCmd.AddBound("@dta", (object?)filter.DependentTableAlias ?? DBNull.Value);
            filterCmd.AddBound("@fk", (object?)filter.ForeignKeyColumn ?? DBNull.Value);
            filterCmd.AddBound("@pk", (object?)filter.PrimaryKeyColumn ?? DBNull.Value);
            filterCmd.AddBound("@sqp", (object?)filter.SubqueryFilterPredicateJson ?? DBNull.Value);
            filterCmd.AddBound("@ttc", (object?)filter.TargetTemporalColumn ?? DBNull.Value);
            filterCmd.AddBound("@dvfc", (object?)filter.DependentValidFromColumn ?? DBNull.Value);
            filterCmd.AddBound("@dvtc", (object?)filter.DependentValidToColumn ?? DBNull.Value);
            filterCmd.AddBound("@tta", (object?)filter.TargetTableAlias ?? DBNull.Value);
            filterCmd.AddBound("@hops", (object?)hopsJson ?? DBNull.Value);
            filterCmd.AddBound("@ksStrategy", filter.KeySetStrategy is { } ksStrategy ? (object)(int)ksStrategy : DBNull.Value);
            filterCmd.AddBound("@ksDuration", (object?)filter.KeySetDurationSeconds ?? DBNull.Value);
            filterCmd.AddBound("@ksMax", (object?)filter.KeySetMaxKeys ?? DBNull.Value);
            await filterCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var auditEntry = new AuditLogEntry
        {
            TenantId = consent.TenantId,
            EventType = "CONSENT_GRANTED",
            ActorSid = consent.CreatedBySid ?? new Sid("SYSTEM"),
            TargetTable = consent.TableIdentifier.ToString(),
            Decision = "GRANTED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consentId,
                GranteeType = consent.GranteeType.ToString(),
                GranteeSid = consent.GranteeSid?.Value,
                ValidFrom = consent.ValidFrom,
                ValidTo = consent.ValidTo
            })
        };

        var created = new Consent
        {
            Id = consentId,
            TableId = tableId,
            TableIdentifier = consent.TableIdentifier,
            ConsentRequestId = consent.ConsentRequestId,
            Effect = consent.Effect,
            GranteeType = consent.GranteeType,
            GranteeSid = consent.GranteeSid,
            RoleId = consent.RoleId,
            RoleName = consent.RoleName,
            ValidFrom = consent.ValidFrom,
            ValidTo = consent.ValidTo,
            IsRevoked = consent.IsRevoked,
            TenantId = consent.TenantId,
            ColumnRules = consent.ColumnRules,
            RowFilters = consent.RowFilters,
            Description = consent.Description
        };

        return (created, auditEntry);
    }

    public static async Task<(TableIdentifier? Table, string? RawTenant)> ReadSystemRevokeTargetAsync(DbConnection conn, DbTransaction? tx, string selectSql, Guid consentId, Guid consentRequestId, CancellationToken ct)
    {
        await using var getCmd = conn.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = selectSql;
        getCmd.AddBound("@id", consentId.ToString());
        getCmd.AddBound("@reqId", consentRequestId.ToString());

        await using var reader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, null);

        var tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        var rawTenant = reader.IsDBNull(3) ? null : reader.GetString(3);
        return (tableId, rawTenant);
    }

    public static async Task<int> ExecuteSystemRevokeUpdateAsync(DbConnection conn, DbTransaction? tx, string updateSql, Guid consentId, Guid consentRequestId, Sid revokedBySid, DateTimeOffset now, string reason, bool bindReqId, CancellationToken ct)
    {
        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = updateSql;
        updateCmd.AddBound("@id", consentId.ToString());
        if (bindReqId) updateCmd.AddBound("@reqId", consentRequestId.ToString());
        updateCmd.AddBound("@sid", revokedBySid.Value);
        updateCmd.AddBound("@now", now.ToString("O"));
        updateCmd.AddBound("@reason", reason);
        return await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<(TableIdentifier? Table, TenantId ExtendTenant, DateTimeOffset NewValidFrom)> ReadAndExtendConsentExpiryAsync(DbConnection conn, DbTransaction? tx, string selectSql, Guid consentId, DateTimeOffset newValidTo, DateTimeOffset now, CancellationToken ct)
    {
        TableIdentifier? tableId = null;
        TenantId extendTenant = default;
        var newValidFrom = now;

        await using (var getCmd = conn.CreateCommand())
        {
            getCmd.Transaction = tx;
            getCmd.CommandText = selectSql;
            getCmd.AddBound("@id", consentId.ToString());
            await using var reader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                extendTenant = ConsentGrantSqlCore.ResolveConsentTenant(reader.IsDBNull(3) ? null : reader.GetString(3), consentId, tableId.Value.ToString());
                newValidFrom = ConsentSubjectFilter.RenewalPeriodStart(reader.GetString(4), now);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE CONSENTS SET valid_to = @validTo, valid_from = @validFrom, renewal_count = renewal_count + 1 WHERE id = @id AND is_revoked = 0 AND valid_to > @now AND @validTo > valid_to";
            cmd.AddBound("@id", consentId.ToString());
            cmd.AddBound("@validTo", newValidTo.ToUniversalTime().ToString("O"));
            cmd.AddBound("@validFrom", newValidFrom.ToUniversalTime().ToString("O"));
            cmd.AddBound("@now", now.ToString("O"));
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new ConsentNotExtendableException(consentId);
            }
        }

        return (tableId, extendTenant, newValidFrom);
    }

    public static AuditLogEntry BuildExpiryExtendedAudit(TenantId extendTenant, TableIdentifier? tableId, Guid consentId, DateTimeOffset newValidTo) =>
        new()
        {
            TenantId = extendTenant,
            EventType = "CONSENT_EXPIRY_EXTENDED",
            ActorSid = new Sid("SYSTEM"),
            TargetTable = tableId?.ToString() ?? "unknown",
            Decision = "EXTENDED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, NewValidTo = newValidTo })
        };

    public static async Task<ConsentRequest> CreateConsentRequestAsync(DbConnection conn, string? findTableIdSql, ConsentRequest request, CancellationToken ct)
    {
        var tableGuid = await ResolveTableIdOrThrowAsync(conn, null, findTableIdSql, request.TableId, request.TableIdentifier, "consent request", ct).ConfigureAwait(false);
        var reqId = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id;
        var reqIdentifiersJson = request.RequesterIdentifiers != null && request.RequesterIdentifiers.Count > 0 ? JsonSerializer.Serialize(request.RequesterIdentifiers) : null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = InsertConsentRequestSql;
            cmd.AddBound("@id", reqId.ToString());
            cmd.AddBound("@tid", tableGuid.ToString());
            cmd.AddBound("@sid", request.RequesterSid.Value);
            cmd.AddBound("@gType", request.RequestedGranteeType.ToString());
            cmd.AddBound("@gRef", request.RequestedGranteeRef);
            cmd.AddBound("@just", request.BusinessJustification);
            cmd.AddBound("@status", request.Status);
            cmd.AddBound("@reqAt", request.RequestedAt.ToString("O"));
            cmd.AddBound("@reqTo", request.RequestedValidTo.ToUniversalTime().ToString("O"));
            cmd.AddBound("@ticket", (object?)request.ItsmTicketId ?? DBNull.Value);
            cmd.AddBound("@tenantId", request.TenantId.Value);
            cmd.AddBound("@reqIds", (object?)reqIdentifiersJson ?? DBNull.Value);
            cmd.AddBound("@ctx", (object?)request.ContextJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new ConsentRequest
        {
            Id = reqId,
            TableId = tableGuid,
            TableIdentifier = request.TableIdentifier,
            RequesterSid = request.RequesterSid,
            RequestedGranteeType = request.RequestedGranteeType,
            RequestedGranteeRef = request.RequestedGranteeRef,
            BusinessJustification = request.BusinessJustification,
            Status = request.Status,
            RequestedAt = request.RequestedAt,
            RequestedValidTo = request.RequestedValidTo,
            ItsmTicketId = request.ItsmTicketId,
            TenantId = request.TenantId,
            RequesterIdentifiers = request.RequesterIdentifiers ?? new List<string>(),
            ApprovalSteps = request.ApprovalSteps ?? new List<ApprovalStep>(),
            ContextJson = request.ContextJson
        };
    }

    public static async Task<ConsentRequest?> ReadConsentRequestAsync(DbConnection conn, DbTransaction? tx, string selectRequestSql, Guid requestId, bool loadSteps, CancellationToken ct)
    {
        ConsentRequest? req = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = selectRequestSql;
            cmd.AddBound("@id", requestId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false)) req = ReadFullConsentRequestRow(reader);
        }

        if (req != null && loadSteps)
        {
            await using var stepCmd = conn.CreateCommand();
            stepCmd.Transaction = tx;
            stepCmd.CommandText = SelectApprovalStepsSql;
            stepCmd.AddBound("@id", requestId.ToString());
            await using var stepReader = await stepCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await stepReader.ReadAsync(ct).ConfigureAwait(false))
            {
                req.ApprovalSteps.Add(new ApprovalStep
                {
                    StepNumber = stepReader.GetInt32(0),
                    ApproverSid = new Sid(stepReader.GetString(1)),
                    Decision = stepReader.GetString(2),
                    RejectionReason = stepReader.IsDBNull(3) ? null : stepReader.GetString(3),
                    DecidedAt = stepReader.IsDBNull(4) ? null : DateTimeOffset.Parse(stepReader.GetString(4))
                });
            }
        }

        return req;
    }

    public static ConsentRequest ReadFullConsentRequestRow(DbDataReader reader)
    {
        var reqIdsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
        var reqIds = !string.IsNullOrWhiteSpace(reqIdsJson) ? JsonSerializer.Deserialize<List<string>>(reqIdsJson) : null;

        return new ConsentRequest
        {
            Id = Guid.Parse(reader.GetString(0)),
            TableId = Guid.Parse(reader.GetString(1)),
            RequesterSid = new Sid(reader.GetString(2)),
            RequestedGranteeType = Enum.Parse<GranteeType>(reader.GetString(3), true),
            RequestedGranteeRef = reader.GetString(4),
            BusinessJustification = reader.GetString(5),
            Status = reader.GetString(6),
            RequestedAt = DateTimeOffset.Parse(reader.GetString(7)),
            RequestedValidTo = DateTimeOffset.Parse(reader.GetString(8)),
            TableIdentifier = new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            ItsmTicketId = reader.IsDBNull(12) ? null : reader.GetString(12),
            TenantId = StoredTenant.Parse(reader.IsDBNull(13) ? null : reader.GetString(13)),
            RequesterIdentifiers = reqIds ?? new List<string>(),
            ContextJson = reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null
        };
    }

    public static ConsentRequest ReadSummaryConsentRequestRow(DbDataReader reader) =>
        new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TableId = Guid.Parse(reader.GetString(1)),
            RequesterSid = new Sid(reader.GetString(2)),
            RequestedGranteeType = Enum.Parse<GranteeType>(reader.GetString(3), true),
            RequestedGranteeRef = reader.GetString(4),
            BusinessJustification = reader.GetString(5),
            Status = reader.GetString(6),
            RequestedAt = DateTimeOffset.Parse(reader.GetString(7)),
            RequestedValidTo = DateTimeOffset.Parse(reader.GetString(8)),
            TableIdentifier = new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            ContextJson = reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null
        };

    public static async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(DbConnection conn, string baseSelectSql, Sid requesterSid, string? status, CancellationToken ct)
    {
        var list = new List<ConsentRequest>();
        await using var cmd = conn.CreateCommand();
        var sql = baseSelectSql;
        if (!string.IsNullOrWhiteSpace(status))
        {
            sql += " AND UPPER(r.status) = @status";
            cmd.AddBound("@status", status.Trim().ToUpperInvariant());
        }

        cmd.CommandText = sql + " ORDER BY r.requested_at DESC";
        cmd.AddBound("@reqSid", requesterSid.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadSummaryConsentRequestRow(reader));
        return list;
    }

    public static async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(DbConnection conn, string selectSql, Sid approverSid, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<ConsentRequest>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.AddBound("@apprSid", approverSid.Value);
        cmd.AddBound("@now", now.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadSummaryConsentRequestRow(reader));
        return list;
    }

    public static async Task<string> ReadRequestStatusOrThrowAsync(DbConnection conn, DbTransaction? tx, string selectStatusSql, Guid requestId, CancellationToken ct)
    {
        await using var checkCmd = conn.CreateCommand();
        checkCmd.Transaction = tx;
        checkCmd.CommandText = selectStatusSql;
        checkCmd.AddBound("@id", requestId.ToString());
        var statusObj = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
        return statusObj.ToString()!;
    }

    public static async Task<(bool RequiresFourEyes, string? Sensitivity, bool SameApprover, int NextStepNumber)> ReadApprovalStateAsync(
        DbConnection conn, DbTransaction? tx, Guid requestId, Sid approverSid, string? itsmApproverAccount,
        IReadOnlyCollection<string> approverOwnerIds, Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyCollection<string>>> resolveOwnerIdsAsync, CancellationToken ct)
    {
        bool requiresFourEyes = false;
        string? tableSensitivity = null;
        await using (var feCmd = conn.CreateCommand())
        {
            feCmd.Transaction = tx;
            feCmd.CommandText = "SELECT t.requires_four_eyes, t.sensitivity FROM TABLES t JOIN CONSENT_REQUESTS r ON t.id = r.table_id WHERE r.id = @id";
            feCmd.AddBound("@id", requestId.ToString());
            await using var reader = await feCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0)) requiresFourEyes = Convert.ToInt32(reader.GetValue(0)) == 1;
                if (!reader.IsDBNull(1)) tableSensitivity = reader.GetString(1);
            }
        }

        var existingApprovers = new List<string>();
        await using (var approversCmd = conn.CreateCommand())
        {
            approversCmd.Transaction = tx;
            approversCmd.CommandText = "SELECT approver_sid FROM APPROVAL_STEPS WHERE consent_request_id = @id AND decision = 'APPROVED' ORDER BY step_number";
            approversCmd.AddBound("@id", requestId.ToString());
            await using var reader = await approversCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) existingApprovers.Add(reader.GetString(0));
        }

        bool sameApprover = existingApprovers.Any(s => ConsentApprovalPolicy.IsSameApprover(s, approverSid, itsmApproverAccount));
        for (int i = 0; !sameApprover && i < existingApprovers.Count; i++)
        {
            var storedOwnerIds = await resolveOwnerIdsAsync(ConsentApprovalPolicy.IdentifierCandidates(existingApprovers[i]), ct).ConfigureAwait(false);
            sameApprover = ConsentApprovalPolicy.ShareOwnerId(storedOwnerIds, approverOwnerIds);
        }

        await using var maxCmd = conn.CreateCommand();
        maxCmd.Transaction = tx;
        maxCmd.CommandText = "SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @id";
        maxCmd.AddBound("@id", requestId.ToString());
        int nextStepNumber = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        return (requiresFourEyes, tableSensitivity, sameApprover, nextStepNumber);
    }

    public static async Task InsertApprovedStepAsync(DbConnection conn, DbTransaction tx, Guid stepId, Guid requestId, int stepNumber, Sid approverSid, DateTimeOffset decidedAt, CancellationToken ct)
    {
        await using var stepInsert = conn.CreateCommand();
        stepInsert.Transaction = tx;
        stepInsert.CommandText = InsertApprovedStepSql;
        stepInsert.AddBound("@id", stepId.ToString());
        stepInsert.AddBound("@reqId", requestId.ToString());
        stepInsert.AddBound("@step", stepNumber);
        stepInsert.AddBound("@sid", approverSid.Value);
        stepInsert.AddBound("@now", decidedAt.ToString("O"));
        await stepInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task InsertRejectedStepAsync(DbConnection conn, DbTransaction tx, Guid requestId, Sid approverSid, string reason, DateTimeOffset decidedAt, CancellationToken ct)
    {
        await using var stepInsert = conn.CreateCommand();
        stepInsert.Transaction = tx;
        stepInsert.CommandText = InsertRejectedStepSql;
        stepInsert.AddBound("@id", Guid.NewGuid().ToString());
        stepInsert.AddBound("@reqId", requestId.ToString());
        stepInsert.AddBound("@sid", approverSid.Value);
        stepInsert.AddBound("@reason", reason);
        stepInsert.AddBound("@now", decidedAt.ToString("O"));
        await stepInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task ApplyGuardedStatusAsync(DbConnection conn, DbTransaction tx, Func<DbConnection, DbTransaction, CancellationToken, Task>? seam, Guid requestId, string newStatus, string expectedStatus, CancellationToken ct)
    {
        if (seam is not null) await seam(conn, tx, ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ConsentApprovalPolicy.GuardedStatusUpdateSql;
        cmd.AddBound("@st", newStatus);
        cmd.AddBound("@id", requestId.ToString());
        cmd.AddBound("@expected", expectedStatus);
        ConsentApprovalPolicy.EnsureTransitionApplied(await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false), requestId);
    }

    public static async Task<bool> ExecuteWithdrawUpdateAsync(DbConnection conn, DbTransaction tx, Guid requestId, Sid requesterSid, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE CONSENT_REQUESTS SET status = 'WITHDRAWN' WHERE id = @id AND requester_sid = @sid AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL')";
        cmd.AddBound("@id", requestId.ToString());
        cmd.AddBound("@sid", requesterSid.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public static async Task<bool> HasConfiguredDataOwnersAsync(DbConnection conn, string countOwnersSql, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = countOwnersSql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result != null && result != DBNull.Value && Convert.ToInt64(result) > 0;
    }

    public static async Task<Guid?> FindRequestIdByTicketIdAsync(DbConnection conn, string selectIdByTicketSql, string ticketId, TenantId? tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectIdByTicketSql;
        cmd.AddBound("@ticket", ticketId);
        if (tenantId.HasValue) cmd.AddBound("@tenant", tenantId.Value.Value);

        var idObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return idObj == null ? null : Guid.Parse(idObj.ToString()!);
    }

    public static async Task<(bool Activated, Guid ConsentId)> TryActivateConsentRowsAsync(DbConnection conn, DbTransaction tx, ConsentRequest req, bool allowPendingAutoApprove, string effectLiteral, DateTimeOffset now, CancellationToken ct)
    {
        await using (var updateReq = conn.CreateCommand())
        {
            updateReq.Transaction = tx;
            updateReq.CommandText = allowPendingAutoApprove
                ? @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                    WHERE id = @id AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL', 'APPROVED')"
                : @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                    WHERE id = @id AND status = 'APPROVED'";
            updateReq.AddBound("@id", req.Id.ToString());
            if (await updateReq.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return (false, Guid.Empty);
        }

        await using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.Transaction = tx;
            existsCmd.CommandText = "SELECT COUNT(1) FROM CONSENTS WHERE consent_request_id = @reqId";
            existsCmd.AddBound("@reqId", req.Id.ToString());
            if (Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0) return (false, Guid.Empty);
        }

        var consentId = Guid.NewGuid();
        var granteeSid = req.RequestedGranteeType != GranteeType.Role ? new Sid(req.RequestedGranteeRef) : (Sid?)null;
        var roleName = req.RequestedGranteeType == GranteeType.Role ? req.RequestedGranteeRef : null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @sidNorm, @roleName, @validFrom, @validTo, 0, @tenantId)";
            cmd.AddBound("@id", consentId.ToString());
            cmd.AddBound("@tid", req.TableId.ToString());
            cmd.AddBound("@reqId", req.Id.ToString());
            cmd.AddBound("@effect", effectLiteral);
            cmd.AddBound("@granteeType", req.RequestedGranteeType.ToString());
            cmd.AddBound("@granteeSid", (object?)granteeSid?.Value ?? DBNull.Value);
            cmd.AddBound("@sidNorm", ConsentSubjectFilter.NormalizeParameter((object?)granteeSid?.Value ?? DBNull.Value));
            cmd.AddBound("@roleName", (object?)roleName ?? DBNull.Value);
            cmd.AddBound("@validFrom", now.ToString("O"));
            cmd.AddBound("@validTo", req.RequestedValidTo.ToUniversalTime().ToString("O"));
            cmd.AddBound("@tenantId", req.TenantId.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var columnRules = new List<(Guid Id, Guid ColId, string ColName, int AccessLevel)>();
        await using (var colCmd = conn.CreateCommand())
        {
            colCmd.Transaction = tx;
            colCmd.CommandText = SelectColumnSnapshotSql;
            colCmd.AddBound("@tid", req.TableId.ToString());
            await using var reader = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var colId = Guid.Parse(reader.GetString(0));
                var colName = reader.GetString(1);
                var isSensitive = Convert.ToInt32(reader.GetValue(2)) == 1;
                var hasMask = Convert.ToInt32(reader.GetValue(3)) == 1;
                int accessLevel = (int)ConsentColumnSnapshot.AccessLevelFor(isSensitive, hasMask);
                columnRules.Add((Guid.NewGuid(), colId, colName, accessLevel));
            }
        }

        foreach (var (ruleId, colId, colName, accessLevel) in columnRules)
        {
            await using var ruleCmd = conn.CreateCommand();
            ruleCmd.Transaction = tx;
            ruleCmd.CommandText = InsertColumnRuleSql;
            ruleCmd.AddBound("@id", ruleId.ToString());
            ruleCmd.AddBound("@consentId", consentId.ToString());
            ruleCmd.AddBound("@colId", colId.ToString());
            ruleCmd.AddBound("@colName", colName);
            ruleCmd.AddBound("@level", accessLevel);
            ruleCmd.AddBound("@scopes", string.Empty);
            await ruleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return (true, consentId);
    }

    public static async Task DeleteConsentRequestRowsAsync(DbConnection conn, DbTransaction? tx, Guid requestId, CancellationToken ct)
    {
        await using (var stepCmd = conn.CreateCommand())
        {
            stepCmd.Transaction = tx;
            stepCmd.CommandText = "DELETE FROM APPROVAL_STEPS WHERE consent_request_id = @id";
            stepCmd.AddBound("@id", requestId.ToString());
            await stepCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM CONSENT_REQUESTS WHERE id = @id";
            cmd.AddBound("@id", requestId.ToString());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public static async Task UpdateConsentRequestTicketIdAsync(DbConnection conn, Guid requestId, string ticketId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE CONSENT_REQUESTS SET itsm_ticket_id = @ticket WHERE id = @id";
        cmd.AddBound("@id", requestId.ToString());
        cmd.AddBound("@ticket", ticketId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
