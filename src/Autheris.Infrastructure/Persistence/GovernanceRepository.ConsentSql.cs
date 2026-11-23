namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>
/// Engine-neutral statement execution and row mapping for consent queries, batch hydration, consent insertion, consent requests,
/// four-eyes approval state reads, guarded status transitions, and ITSM activation (folded from ConsentSqlCore.cs).
/// </summary>
// Transitional: transitional remnant of the per-engine SQL cores; fold into GovernanceRepository.Consent.
internal static class ConsentSqlCore
{
    public const int ConsentHydrateBatchSize = 500;

    public const string SelectColumnRulesPrefix = "SELECT id, consent_id, table_column_id, column_name, access_level, applicable_scopes FROM CONSENT_COLUMN_RULES WHERE consent_id IN (";

    public const string SelectRowFiltersPrefix = @"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                   operator, value_type, value_json, value_source, user_attribute,
                                   filter_type
                            FROM CONSENT_ROW_FILTERS WHERE consent_id IN (";

    public const string SelectRowScopesPrefix = "SELECT consent_id, policy_name, parameters_json, column_map_json, uncovered FROM CONSENT_ROW_SCOPES WHERE consent_id IN (";

    public const string InsertColumnRuleSql = "INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level, applicable_scopes) VALUES (@id, @consentId, @colId, @colName, @level, @scopes)";

    public const string InsertRowFilterSql = @"INSERT INTO CONSENT_ROW_FILTERS (
                                        id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                                        filter_type
                                    ) VALUES (
                                        @id, @consentId, @fg, @colId, @colName, @op, @vType, @vJson, @vSource, @uAttr,
                                        @ft)";

    public const string InsertRowScopeSql = "INSERT INTO CONSENT_ROW_SCOPES (tenant_id, consent_id, entry_no, policy_name, parameters_json, column_map_json, uncovered) VALUES (@tenantId, @consentId, @entryNo, @policyName, @paramsJson, @columnMapJson, @uncovered)";

    public const string InsertConsentRequestSql = @"INSERT INTO CONSENT_REQUESTS (
                                    id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref,
                                    business_justification, status, requested_at, requested_valid_to,
                                    tenant_id, requester_identifiers_json, context_json, requested_write_rules_json,
                                    version, workflow_mode, external_system_id, external_reference, requested_valid_from, updated_at, request_kind, profile_id,
                                    grant_kind, target_columns, container_kind, container_id, approver_capacity, self_grant, bundle_id, decided_by, decided_at
                                ) VALUES (@id, @tid, @sid, @gType, @gRef, @just, @status, @reqAt, @reqTo, @tenantId, @reqIds, @ctx, @reqWriteRules, @version, @workflowMode, @externalSystemId, @externalReference, @reqValidFrom, @updatedAt, @requestKind, @profileId, @grantKind, @targetColumns, @containerKind, @containerId, @approverCapacity, @selfGrant, @bundleId, @decidedBy, @decidedAt)";

    public const string SelectApprovalStepsSql = "SELECT step_number, approver_sid, decision, rejection_reason, decided_at FROM APPROVAL_STEPS WHERE consent_request_id = @id ORDER BY step_number ASC";

    public const string InsertApprovedStepSql = "INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, decided_at) VALUES (@id, @reqId, @step, @sid, 'APPROVED', @now)";

    public const string InsertRejectedStepSql = "INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, rejection_reason, decided_at) VALUES (@id, @reqId, (SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @reqId), @sid, 'REJECTED', @reason, @now)";

    public const string SelectColumnSnapshotSql = @"SELECT c.id, c.column_name,
                                       CASE WHEN EXISTS (SELECT 1 FROM COLUMN_CLASSIFICATIONS cc WHERE cc.table_column_id = c.id AND cc.scope = '*' AND cc.code <> 'PUBLIC') THEN 1 ELSE 0 END AS is_classified,
                                       CASE WHEN EXISTS (SELECT 1 FROM COLUMN_MASK_OVERRIDES m WHERE m.table_column_id = c.id AND m.scope = '*' AND m.code IS NULL) THEN 1 ELSE 0 END AS has_mask
                                       FROM TABLE_COLUMNS c WHERE c.table_id = @tid";

    public static void ReadSubjectConsentRows(DbDataReader reader, TableIdentifier table, DateTimeOffset atTime, HashSet<Sid> subjectSet, HashSet<Guid> seen, List<Consent> consents)
    {
        while (reader.Read())
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
            if (atTime >= validTo) continue;

            var gEffectStr = reader.GetString(3);
            var isDeny = Enum.TryParse<ConsentEffect>(gEffectStr, true, out var parsedEffect) && parsedEffect == ConsentEffect.Deny;
            if (atTime < validFrom && !isDeny) continue;

            var granteeType = GranteeTypeParsing.ParseStored(reader.GetString(4));
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (granteeSidStr == null || !subjectSet.Contains(new Sid(granteeSidStr))) continue;

            var id = Guid.Parse(reader.GetString(0));
            if (!seen.Add(id)) continue;

            consents.Add(ConsentWorkflowSql.BuildConsent(
                id,
                Guid.Parse(reader.GetString(1)),
                table,
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Enum.Parse<ConsentEffect>(gEffectStr, true),
                granteeType,
                granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                validFrom,
                validTo,
                reader.GetInt32(8) == 1,
                StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
                reader,
                13));
        }
    }

    public static void ReadAllSubjectConsentRows(DbDataReader reader, DateTimeOffset effectiveAt, HashSet<Sid> subjectSet, HashSet<Guid> seen, List<Consent> consents)
    {
        while (reader.Read())
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
            if (effectiveAt < validFrom || effectiveAt >= validTo) continue;

            var granteeType = GranteeTypeParsing.ParseStored(reader.GetString(4));
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (granteeSidStr == null || !subjectSet.Contains(new Sid(granteeSidStr))) continue;

            var id = Guid.Parse(reader.GetString(0));
            if (!seen.Add(id)) continue;

            consents.Add(ConsentWorkflowSql.BuildConsent(
                id,
                Guid.Parse(reader.GetString(1)),
                new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                granteeType,
                granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                validFrom,
                validTo,
                reader.GetInt32(8) == 1,
                StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
                reader,
                13));
        }
    }

    public static async Task<List<Consent>> ReadActiveConsentsByRequestIdAsync(DbConnection conn, Guid consentRequestId, DateTimeOffset atTime, bool includeNotYetValid, CancellationToken ct)
    {
        var consents = new List<Consent>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id,
                                   c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_stage, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.consent_request_id = @reqId";
        cmd.AddBound("@reqId", consentRequestId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture);
            if (atTime >= validTo || (!includeNotYetValid && atTime < validFrom)) continue;

            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            consents.Add(ConsentWorkflowSql.BuildConsent(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeTypeParsing.ParseStored(reader.GetString(4)),
                granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                validFrom,
                validTo,
                reader.GetInt32(8) == 1,
                StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
                reader,
                13));
        }

        return consents;
    }

    public static async Task<Consent?> ReadConsentByIdRowAsync(DbConnection conn, Guid consentId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.valid_from, c.valid_to,
                                   c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                   t.source_name, t.schema_name, t.table_name, c.tenant_id, c.renewal_count,
                                   c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_stage, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.id = @id";
        cmd.AddBound("@id", consentId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
        return ConsentWorkflowSql.BuildConsent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            new TableIdentifier(reader.GetString(12), reader.GetString(13), reader.GetString(14)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Enum.Parse<ConsentEffect>(reader.GetString(3), true),
            GranteeTypeParsing.ParseStored(reader.GetString(4)),
            granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetInt32(8) == 1,
            StoredTenant.Parse(reader.IsDBNull(15) ? null : reader.GetString(15)),
            reader,
            17,
            renewalCount: reader.GetInt32(16),
            revokedBySid: reader.IsDBNull(9) ? (Sid?)null : new Sid(reader.GetString(9)),
            revokedAt: reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            revokeReason: reader.IsDBNull(11) ? null : reader.GetString(11));
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
            consents.Add(ConsentWorkflowSql.BuildConsent(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeTypeParsing.ParseStored(reader.GetString(4)),
                granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetInt32(8) == 1,
                StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
                reader,
                14,
                renewalCount: reader.GetInt32(13)));
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

    public static async Task LoadDescriptionsBatchAsync(DbConnection conn, IReadOnlyCollection<Consent> consents, CancellationToken ct)
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
        var writeRulesMap = consents.GroupBy(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new List<WriteRule>(), StringComparer.OrdinalIgnoreCase);
        var rowScopesMap = consents.GroupBy(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new List<GrantRowScopeEntry>(), StringComparer.OrdinalIgnoreCase);

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

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ConsentWriteRulesSql.SelectWriteRulesPrefix + BindIdInClause(cmd, "cid", batch) + ")";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var cId = reader.GetString(1);
                    if (writeRulesMap.TryGetValue(cId, out var writeRuleList)) writeRuleList.Add(ConsentWriteRulesSql.ReadWriteRule(reader));
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectRowScopesPrefix + BindIdInClause(cmd, "cid", batch) + ") ORDER BY consent_id, entry_no";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var cId = reader.GetString(0);
                    if (rowScopesMap.TryGetValue(cId, out var scopeList)) scopeList.Add(ReadRowScope(reader));
                }
            }
        }

        foreach (var c in consents)
        {
            var key = c.Id.ToString();
            c.ColumnRules = rulesMap[key];
            c.RowFilters = filtersMap[key];
            c.WriteRules = writeRulesMap[key];
            c.RowScope = rowScopesMap[key];
        }

        await GovernanceRepository.HydrateConsentClassificationAccessBatchAsync(conn, consents, ct).ConfigureAwait(false);
    }

    public static ConsentColumnRule ReadColumnRule(DbDataReader reader, string consentIdStr)
    {
        var rawLevel = reader.GetInt32(4);
        var scopesRaw = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : null;
        var scopes = string.IsNullOrWhiteSpace(scopesRaw)
            ? Array.Empty<string>()
            : scopesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parsedLevel = (ColumnAccessLevel)rawLevel;
        return new ConsentColumnRule
        {
            Id = Guid.Parse(reader.GetString(0)),
            ConsentId = Guid.Parse(consentIdStr),
            TableColumnId = Guid.Parse(reader.GetString(2)),
            ColumnName = reader.GetString(3),
            AccessLevel = parsedLevel is ColumnAccessLevel.Deny or ColumnAccessLevel.Mask or ColumnAccessLevel.Clear
                ? parsedLevel
                : ColumnAccessLevel.Deny,
            ApplicableScopes = scopes
        };
    }

    public static ConsentRowFilter ReadRowFilter(DbDataReader reader, string consentIdStr)
    {
        var ftInt = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
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
            FilterType = (RowFilterType)ftInt
        };
    }

    public static GrantRowScopeEntry ReadRowScope(DbDataReader reader)
    {
        var policyName = reader.GetString(1);
        var paramsJson = reader.IsDBNull(2) ? "{}" : reader.GetString(2);
        var parameters = string.IsNullOrWhiteSpace(paramsJson)
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsJson) ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var columnMap = reader.IsDBNull(3) || string.IsNullOrWhiteSpace(reader.GetString(3))
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(3));
        var uncoveredRaw = reader.IsDBNull(4) ? "DENY" : reader.GetString(4);
        var uncovered = string.Equals(uncoveredRaw, "SKIP", StringComparison.OrdinalIgnoreCase)
            ? UncoveredPolicy.Skip
            : UncoveredPolicy.Deny;
        return new GrantRowScopeEntry(policyName, parameters, columnMap, uncovered);
    }

    public static async Task InsertColumnRulesAndRowFiltersAsync(DbConnection conn, DbTransaction tx, Consent consent, CancellationToken ct)
    {
        foreach (var rule in consent.ColumnRules)
        {
            var ruleId = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
            await using var ruleCmd = conn.CreateCommand();
            ruleCmd.Transaction = tx;
            ruleCmd.CommandText = InsertColumnRuleSql;
            ruleCmd.AddBound("@id", ruleId.ToString());
            ruleCmd.AddBound("@consentId", consent.Id.ToString());
            ruleCmd.AddBound("@colId", rule.TableColumnId.ToString());
            ruleCmd.AddBound("@colName", rule.ColumnName);
            ruleCmd.AddBound("@level", (int)rule.AccessLevel);
            ruleCmd.AddBound("@scopes", rule.ApplicableScopes != null && rule.ApplicableScopes.Count > 0 ? string.Join(",", rule.ApplicableScopes) : string.Empty);
            await ruleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var filter in consent.RowFilters)
        {
            var filterId = filter.Id == Guid.Empty ? Guid.NewGuid() : filter.Id;

            await using var filterCmd = conn.CreateCommand();
            filterCmd.Transaction = tx;
            filterCmd.CommandText = InsertRowFilterSql;
            filterCmd.AddBound("@id", filterId.ToString());
            filterCmd.AddBound("@consentId", consent.Id.ToString());
            filterCmd.AddBound("@fg", filter.FilterGroup);
            filterCmd.AddBound("@colId", filter.TableColumnId.ToString());
            filterCmd.AddBound("@colName", filter.ColumnName);
            filterCmd.AddBound("@op", filter.Operator);
            filterCmd.AddBound("@vType", filter.ValueType);
            filterCmd.AddBound("@vJson", filter.ValueJson);
            filterCmd.AddBound("@vSource", filter.ValueSource);
            filterCmd.AddBound("@uAttr", (object?)filter.UserAttribute ?? DBNull.Value);
            filterCmd.AddBound("@ft", (int)filter.FilterType);
            await filterCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (int i = 0; i < consent.RowScope.Count; i++)
        {
            var scope = consent.RowScope[i];
            await using var scopeCmd = conn.CreateCommand();
            scopeCmd.Transaction = tx;
            scopeCmd.CommandText = InsertRowScopeSql;
            scopeCmd.AddBound("@tenantId", StoredTenant.RequireWritable(consent.TenantId));
            scopeCmd.AddBound("@consentId", consent.Id.ToString());
            scopeCmd.AddBound("@entryNo", i);
            scopeCmd.AddBound("@policyName", scope.PolicyName);
            scopeCmd.AddBound("@paramsJson", JsonSerializer.Serialize(scope.Parameters ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
            scopeCmd.AddBound("@columnMapJson", scope.ColumnMap is { Count: > 0 } ? JsonSerializer.Serialize(scope.ColumnMap) : DBNull.Value);
            scopeCmd.AddBound("@uncovered", scope.Uncovered == UncoveredPolicy.Skip ? "SKIP" : "DENY");
            await scopeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ConsentWriteRulesSql.InsertWriteRulesAsync(conn, tx, consent, ct).ConfigureAwait(false);
        await GovernanceRepository.InsertConsentClassificationAccessAsync(conn, tx, consent, ct).ConfigureAwait(false);
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

    public static async Task<(TableIdentifier? Table, string? RawTenant, Consent? Consent)> ReadSystemRevokeTargetAsync(DbConnection conn, DbTransaction? tx, string selectSql, Guid consentId, Guid consentRequestId, CancellationToken ct)
    {
        await using var getCmd = conn.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = selectSql;
        getCmd.AddBound("@id", consentId.ToString());
        getCmd.AddBound("@reqId", consentRequestId.ToString());

        await using var reader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, null, null);

        var tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        var rawTenant = reader.IsDBNull(3) ? null : reader.GetString(3);
        var tableGuid = reader.FieldCount > 4 && !reader.IsDBNull(4) ? Guid.Parse(reader.GetString(4)) : Guid.Empty;
        var effect = reader.FieldCount > 5 && !reader.IsDBNull(5) ? Enum.Parse<ConsentEffect>(reader.GetString(5), true) : ConsentEffect.Allow;
        var granteeType = reader.FieldCount > 6 && !reader.IsDBNull(6) ? GranteeTypeParsing.ParseStored(reader.GetString(6)) : GranteeType.User;
        var granteeSidStr = ConsentWorkflowSql.ReadOptionalString(reader, 7);
        var grantSource = ConsentWorkflowSql.ReadGrantSource(reader, 8);
        var profileAssignmentId = ConsentWorkflowSql.ReadOptionalGuid(reader, 9);
        var validFrom = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 10) ?? DateTimeOffset.MinValue;
        var validTo = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 11) ?? DateTimeOffset.MaxValue;
        var consent = new Consent
        {
            Id = consentId,
            TableId = tableGuid,
            TableIdentifier = tableId,
            ConsentRequestId = consentRequestId,
            Effect = effect,
            TenantId = TenantId.TryParse(rawTenant, out var parsedTenant) ? parsedTenant : default,
            GranteeType = granteeType,
            GranteeSid = !string.IsNullOrWhiteSpace(granteeSidStr) ? new Sid(granteeSidStr) : (Sid?)null,
            ValidFrom = validFrom,
            ValidTo = validTo,
            GrantSource = grantSource,
            ProfileAssignmentId = profileAssignmentId
        };
        return (tableId, rawTenant, consent);
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

    public static async Task<ConsentRequest> CreateConsentRequestAsync(DbConnection conn, string? findTableIdSql, ConsentRequest request, CancellationToken ct)
    {
        var skipTableResolution = request.RequestKind is ConsentRequestKind.ProfileAssignment or ConsentRequestKind.Catalog
            && request.TableId == Guid.Empty
            && string.IsNullOrWhiteSpace(request.TableIdentifier.TableName);
        var tableGuid = skipTableResolution
            ? Guid.Empty
            : await ResolveTableIdOrThrowAsync(conn, null, findTableIdSql, request.TableId, request.TableIdentifier, "consent request", ct).ConfigureAwait(false);
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
            cmd.AddBound("@tenantId", request.TenantId.Value);
            cmd.AddBound("@reqIds", (object?)reqIdentifiersJson ?? DBNull.Value);
            cmd.AddBound("@ctx", (object?)request.ContextJson ?? DBNull.Value);
            cmd.AddBound("@reqWriteRules", (object?)ConsentWriteRulesSql.SerializeRequestedWriteRules(request) ?? DBNull.Value);
            ConsentWorkflowSql.BindRequestParameters(cmd, request);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return ConsentWorkflowSql.BuildCreatedRequest(request, reqId, tableGuid);
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
        var reqIdsJson = reader.IsDBNull(13) ? null : reader.GetString(13);
        var reqIds = !string.IsNullOrWhiteSpace(reqIdsJson) ? JsonSerializer.Deserialize<List<string>>(reqIdsJson) : null;
        var (writeRules, includeRead) = ConsentWriteRulesSql.DeserializeRequestedWriteRules(
            reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null);

        return ConsentWorkflowSql.BuildConsentRequest(
            reader,
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            new Sid(reader.GetString(2)),
            GranteeTypeParsing.ParseStored(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? default : new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
            reqIds ?? new List<string>(),
            reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
            writeRules,
            includeRead,
            16);
    }

    public static ConsentRequest ReadSummaryConsentRequestRow(DbDataReader reader)
    {
        var (writeRules, includeRead) = ConsentWriteRulesSql.DeserializeRequestedWriteRules(
            reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null);
        var tenant = reader.FieldCount > 14 + ConsentWorkflowSql.RequestColumnCount && !reader.IsDBNull(14 + ConsentWorkflowSql.RequestColumnCount)
            ? StoredTenant.Parse(reader.GetString(14 + ConsentWorkflowSql.RequestColumnCount))
            : default;
        return ConsentWorkflowSql.BuildConsentRequest(
            reader,
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            new Sid(reader.GetString(2)),
            GranteeTypeParsing.ParseStored(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? default : new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            tenant,
            new List<string>(),
            reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetString(12) : null,
            writeRules,
            includeRead,
            14);
    }

    public static async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(DbConnection conn, string baseSelectSql, Sid requesterSid, TenantId tenantId, string? status, CancellationToken ct)
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
        cmd.AddBound("@tenantId", tenantId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadSummaryConsentRequestRow(reader));
        return list;
    }

    public static async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(DbConnection conn, string selectSql, Sid approverSid, TenantId tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var list = new List<ConsentRequest>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.AddBound("@apprSid", approverSid.Value);
        cmd.AddBound("@tenantId", tenantId.Value);
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
        DbConnection conn, DbTransaction? tx, Guid requestId, Sid approverSid,
        IReadOnlyCollection<string> approverOwnerIds, Func<IReadOnlyList<string>, Task<IReadOnlyCollection<string>>> resolveOwnerIdsAsync, CancellationToken ct)
    {
        var existingApprovers = new List<string>();
        await using (var approversCmd = conn.CreateCommand())
        {
            approversCmd.Transaction = tx;
            approversCmd.CommandText = "SELECT approver_sid FROM APPROVAL_STEPS WHERE consent_request_id = @id AND decision = 'APPROVED' ORDER BY step_number";
            approversCmd.AddBound("@id", requestId.ToString());
            await using var reader = await approversCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) existingApprovers.Add(reader.GetString(0));
        }

        bool sameApprover = existingApprovers.Any(s => ConsentApprovalPolicy.IsSameApprover(s, approverSid));
        for (int i = 0; !sameApprover && i < existingApprovers.Count; i++)
        {
            var storedOwnerIds = await resolveOwnerIdsAsync(ConsentApprovalPolicy.IdentifierCandidates(existingApprovers[i])).ConfigureAwait(false);
            sameApprover = ConsentApprovalPolicy.ShareOwnerId(storedOwnerIds, approverOwnerIds);
        }

        await using var maxCmd = conn.CreateCommand();
        maxCmd.Transaction = tx;
        maxCmd.CommandText = "SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @id";
        maxCmd.AddBound("@id", requestId.ToString());
        int nextStepNumber = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

        return (false, null, sameApprover, nextStepNumber);
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

    public static async Task ApplyGuardedStatusAsync(
        DbConnection conn,
        DbTransaction tx,
        Func<DbConnection, DbTransaction, CancellationToken, Task>? seam,
        Guid requestId,
        string newStatus,
        string expectedStatus,
        CancellationToken ct,
        DateTimeOffset? updatedAt = null)
    {
        if (seam is not null) await seam(conn, tx, ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ConsentApprovalPolicy.GuardedStatusUpdateSql;
        cmd.AddBound("@st", newStatus);
        cmd.AddBoundTimestamp("@updatedAt", updatedAt ?? TimeProvider.System.GetUtcNow());
        cmd.AddBound("@id", requestId.ToString());
        cmd.AddBound("@expected", expectedStatus);
        ConsentApprovalPolicy.EnsureTransitionApplied(await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false), requestId);
    }

    public static async Task<bool> ExecuteWithdrawUpdateAsync(DbConnection conn, DbTransaction tx, Guid requestId, Sid requesterSid, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE CONSENT_REQUESTS SET status = 'WITHDRAWN' WHERE id = @id AND requester_sid = @sid AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')";
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

    public static async Task<Guid?> FindRequestIdByExternalReferenceAsync(
        DbConnection conn,
        Guid externalSystemId,
        string externalReference,
        TenantId tenantId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM CONSENT_REQUESTS WHERE external_system_id = @sysId AND external_reference = @extRef AND tenant_id = @tenant ORDER BY requested_at, id";
        cmd.AddBound("@sysId", externalSystemId.ToString());
        cmd.AddBound("@extRef", externalReference);
        cmd.AddBound("@tenant", tenantId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Guid.Parse(reader.GetString(0)) : null;
    }

    public static async Task<(bool Claimed, Guid ConsentId, Consent? DraftConsent)> TryClaimConsentRequestActivationAsync(
        DbConnection conn, DbTransaction tx, ConsentRequest req, bool allowPendingAutoApprove, string effectLiteral, DateTimeOffset now, CancellationToken ct)
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
            if (await updateReq.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return (false, Guid.Empty, null);
        }

        await using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.Transaction = tx;
            existsCmd.CommandText = "SELECT COUNT(1) FROM CONSENTS WHERE consent_request_id = @reqId";
            existsCmd.AddBound("@reqId", req.Id.ToString());
            if (Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0) return (false, Guid.Empty, null);
        }

        var consentId = Guid.NewGuid();
        var granteeSid = new Sid(req.RequestedGranteeRef);
        var grantSource = req.RequestKind == ConsentRequestKind.ProfileAssignment
            ? ConsentGrantSource.AccessProfile
            : req.SelfGrant
                ? ConsentGrantSource.SelfGrant
                : ConsentGrantSource.ConsentRequest;
        Guid? profileAssignmentId = req.RequestKind == ConsentRequestKind.ProfileAssignment ? req.Id : null;

        var draft = new Consent
        {
            Id = consentId,
            TableId = req.TableId,
            TableIdentifier = req.TableIdentifier,
            ConsentRequestId = req.Id,
            Effect = string.Equals(effectLiteral, "DENY", StringComparison.OrdinalIgnoreCase) ? ConsentEffect.Deny : ConsentEffect.Allow,
            TenantId = req.TenantId,
            GranteeType = req.RequestedGranteeType,
            GranteeSid = granteeSid,
            CreatedBySid = req.RequesterSid,
            ValidFrom = now,
            ValidTo = req.RequestedValidTo.ToUniversalTime(),
            GrantSource = grantSource,
            ProfileAssignmentId = profileAssignmentId,
            GrantKind = req.GrantKind,
            ApproverCapacity = req.ApproverCapacity,
            SelfGrant = req.SelfGrant,
            TargetColumns = req.TargetColumns?.ToArray()
        };
        return (true, consentId, draft);
    }

    public static async Task InsertActivatedColumnAndWriteRulesAsync(
        DbConnection conn, DbTransaction tx, ConsentRequest req, Guid consentId, CancellationToken ct)
    {
        if (req.ShouldGrantReadColumns)
        {
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
                    var classified = Convert.ToInt32(reader.GetValue(2)) == 1;
                    var hasMask = Convert.ToInt32(reader.GetValue(3)) == 1;
                    int accessLevel = (int)ConsentColumnSnapshot.AccessLevelFor(classified, hasMask);
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
        }

        if (req.RequestedWriteRules.Count > 0)
        {
            var writeConsent = new Consent
            {
                Id = consentId,
                TableId = req.TableId,
                TableIdentifier = req.TableIdentifier,
                TenantId = req.TenantId,
                WriteRules = req.RequestedWriteRules
            };
            await ConsentWriteRulesSql.InsertWriteRulesAsync(conn, tx, writeConsent, ct).ConfigureAwait(false);
        }
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
}
