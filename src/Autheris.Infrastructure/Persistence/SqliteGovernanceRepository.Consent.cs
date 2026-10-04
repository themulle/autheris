using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        CancellationToken ct = default) =>
        GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        var subjectSet = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0) return Array.Empty<Consent>();

        await _lock.WaitAsync(ct);
        try
        {
            var consents = new List<Consent>();
            using (var cmd = _connection.CreateCommand())
            {
                var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
                                      AND c.is_revoked = 0" + tenantFilter;
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);
                if (tenantId != null)
                {
                    cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
                }

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                    var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);

                    if (atTime < validFrom || atTime >= validTo) continue;

                    var gTypeStr = reader.GetString(4);
                    var granteeType = Enum.Parse<GranteeType>(gTypeStr, true);
                    var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                    if (granteeType != GranteeType.Role && (granteeSidStr == null || !subjectSet.Contains(granteeSidStr)))
                    {
                        continue;
                    }

                    var consent = new Consent
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = Guid.Parse(reader.GetString(1)),
                        TableIdentifier = table,
                        ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                        GranteeType = granteeType,
                        GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                        RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                        RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        ValidFrom = validFrom,
                        ValidTo = validTo,
                        IsRevoked = reader.GetInt32(10) == 1,
                        TenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14))
                    };
                    consents.Add(consent);
                }
            }

            // Hydrate column rules & row filters in batch (K-01)
            await HydrateConsentsBatchAsync(consents, ct);

            return consents;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles = null,
        DateTimeOffset? atTime = null,
        CancellationToken ct = default) =>
        GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, null, ct);

    public async Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles,
        DateTimeOffset? atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        var effectiveAt = atTime ?? DateTimeOffset.UtcNow;
        var subjectList = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roleSet = roles != null
            ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await _lock.WaitAsync(ct);
        try
        {
            var consents = new List<Consent>();
            using (var cmd = _connection.CreateCommand())
            {
                var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.is_revoked = 0" + tenantFilter;
                if (tenantId != null)
                {
                    cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
                }

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                    var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);

                    if (effectiveAt < validFrom || effectiveAt >= validTo) continue;

                    var gTypeStr = reader.GetString(4);
                    var granteeType = Enum.Parse<GranteeType>(gTypeStr, true);
                    var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                    if (granteeType == GranteeType.Role)
                    {
                        var roleName = reader.IsDBNull(7) ? null : reader.GetString(7);
                        if (roleName == null || !roleSet.Contains(roleName))
                        {
                            continue;
                        }
                    }
                    else if (granteeSidStr == null || !subjectList.Contains(granteeSidStr))
                    {
                        continue;
                    }

                    var domain = reader.GetString(11);
                    var tableIdentifier = new TableIdentifier(domain, reader.GetString(12), reader.GetString(13));

                    var consent = new Consent
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = Guid.Parse(reader.GetString(1)),
                        TableIdentifier = tableIdentifier,
                        ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                        GranteeType = granteeType,
                        GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                        RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                        RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        ValidFrom = validFrom,
                        ValidTo = validTo,
                        IsRevoked = reader.GetInt32(10) == 1,
                        TenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14))
                    };
                    consents.Add(consent);
                }
            }

            // Hydrate column rules & row filters in batch (K-01)
            await HydrateConsentsBatchAsync(consents, ct);

            return consents;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// SEC E-05 / EX-02: Loads every active consent with the given consent_request_id (sync marker) regardless of
    /// grantee, table or tenant, so that a reconcile can revoke consents of deleted grantees / filtered tables.
    /// </summary>
    public async Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(
        Guid consentRequestId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var consents = new List<Consent>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.is_revoked = 0 AND c.consent_request_id = @reqId";
                cmd.Parameters.AddWithValue("@reqId", consentRequestId.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                    var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);

                    // Not-yet-valid consents are included on purpose (they would become active later); expired ones are not.
                    if (atTime >= validTo) continue;

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
                        TenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14))
                    });
                }
            }

            return consents;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// SEC E-05 / EX-02: Revokes a sync-owned consent (consent_request_id = marker) without data-owner check. Manual or
    /// workflow consents never match the marker and therefore cannot be revoked through this path.
    /// </summary>
    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            TableIdentifier? tableId = null;
            TenantId tenantId = TenantId.LegacySingleTenant;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@reqId", consentRequestId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    tenantId = reader.IsDBNull(3) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(3));
                }
            }

            if (!tableId.HasValue)
            {
                return false;
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"UPDATE CONSENTS
                                    SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                    WHERE id = @id AND consent_request_id = @reqId AND is_revoked = 0";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@reqId", consentRequestId.ToString());
                cmd.Parameters.AddWithValue("@by", revokedBySid.Value);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@reason", reason);
                if (await cmd.ExecuteNonQueryAsync(ct) != 1)
                {
                    return false;
                }
            }

            await IncrementTableEpochInternalAsync(tableId.Value, ct);

            await RecordAuditEventInternalAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = "CONSENT_REVOKED",
                ActorSid = revokedBySid,
                TargetTable = tableId.Value.ToString(),
                Decision = "REVOKED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consentId,
                    Reason = reason,
                    SystemRevocation = true
                })
            }, ct);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<ConsentColumnRule>> LoadColumnRulesAsync(Guid consentId, CancellationToken ct)
    {
        var rules = new List<ConsentColumnRule>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT id, consent_id, table_column_id, column_name, access_level
                            FROM CONSENT_COLUMN_RULES WHERE consent_id = @cid";
        cmd.Parameters.AddWithValue("@cid", consentId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rules.Add(new ConsentColumnRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                ConsentId = Guid.Parse(reader.GetString(1)),
                TableColumnId = Guid.Parse(reader.GetString(2)),
                ColumnName = reader.GetString(3),
                AccessLevel = (ColumnAccessLevel)reader.GetInt32(4)
            });
        }
        return rules;
    }

    private async Task<IReadOnlyList<ConsentRowFilter>> LoadRowFiltersAsync(Guid consentId, CancellationToken ct)
    {
        var filters = new List<ConsentRowFilter>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                   operator, value_type, value_json, value_source, user_attribute,
                                   filter_type, dependent_table, dependent_table_alias, foreign_key_column,
                                   primary_key_column, subquery_predicate_json, target_temporal_column,
                                   dependent_valid_from_column, dependent_valid_to_column, target_table_alias,
                                   additional_hops_json
                            FROM CONSENT_ROW_FILTERS WHERE consent_id = @cid";
        cmd.Parameters.AddWithValue("@cid", consentId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            TableIdentifier? depTable = null;
            if (!reader.IsDBNull(11))
            {
                var depStr = reader.GetString(11);
                if (TableIdentifier.TryParse(depStr, out var parsed))
                {
                    depTable = parsed;
                }
            }

            IReadOnlyList<SubqueryJoinHop>? hops = null;
            if (!reader.IsDBNull(20))
            {
                var hopsJson = reader.GetString(20);
                if (!string.IsNullOrWhiteSpace(hopsJson))
                {
                    try
                    {
                        hops = System.Text.Json.JsonSerializer.Deserialize<List<SubqueryJoinHop>>(hopsJson);
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        throw new InvalidOperationException("Malformed additional hops JSON detected in consent row filter.", ex);
                    }
                }
            }

            filters.Add(new ConsentRowFilter
            {
                Id = Guid.Parse(reader.GetString(0)),
                ConsentId = Guid.Parse(reader.GetString(1)),
                FilterGroup = reader.GetInt32(2),
                TableColumnId = Guid.Parse(reader.GetString(3)),
                ColumnName = reader.GetString(4),
                Operator = reader.GetString(5),
                ValueType = reader.GetString(6),
                ValueJson = reader.GetString(7),
                ValueSource = reader.GetString(8),
                UserAttribute = reader.IsDBNull(9) ? null : reader.GetString(9),
                FilterType = (RowFilterType)(reader.IsDBNull(10) ? 0 : reader.GetInt32(10)),
                DependentTable = depTable,
                DependentTableAlias = reader.IsDBNull(12) ? null : reader.GetString(12),
                ForeignKeyColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
                PrimaryKeyColumn = reader.IsDBNull(14) ? null : reader.GetString(14),
                SubqueryFilterPredicateJson = reader.IsDBNull(15) ? null : reader.GetString(15),
                TargetTemporalColumn = reader.IsDBNull(16) ? null : reader.GetString(16),
                DependentValidFromColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
                DependentValidToColumn = reader.IsDBNull(18) ? null : reader.GetString(18),
                TargetTableAlias = reader.IsDBNull(19) ? null : reader.GetString(19),
                AdditionalHops = hops
            });
        }
        return filters;
    }

    private async Task HydrateConsentsBatchAsync(List<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;

        if (consents.Count == 1)
        {
            var single = consents[0];
            single.ColumnRules = await LoadColumnRulesAsync(single.Id, ct);
            single.RowFilters = await LoadRowFiltersAsync(single.Id, ct);
            return;
        }

        // Chunk by 500 to protect SQLite 999 parameter limit
        foreach (var consentChunk in consents.Chunk(500))
        {
            var idList = string.Join(",", consentChunk.Select((_, i) => $"@cid{i}"));

            // 1. Batch load column rules
            using (var cmdRules = _connection.CreateCommand())
            {
                cmdRules.CommandText = $@"SELECT id, consent_id, table_column_id, column_name, access_level
                                         FROM CONSENT_COLUMN_RULES WHERE consent_id IN ({idList})";
                for (int i = 0; i < consentChunk.Length; i++)
                {
                    cmdRules.Parameters.AddWithValue($"@cid{i}", consentChunk[i].Id.ToString());
                }

                var rulesMap = consentChunk.ToDictionary(c => c.Id, _ => new List<ConsentColumnRule>());
                using var reader = await cmdRules.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var cid = Guid.Parse(reader.GetString(1));
                    if (rulesMap.TryGetValue(cid, out var list))
                    {
                        list.Add(new ConsentColumnRule
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            ConsentId = cid,
                            TableColumnId = Guid.Parse(reader.GetString(2)),
                            ColumnName = reader.GetString(3),
                            AccessLevel = (ColumnAccessLevel)reader.GetInt32(4)
                        });
                    }
                }

                foreach (var c in consentChunk)
                {
                    c.ColumnRules = rulesMap[c.Id];
                }
            }

            // 2. Batch load row filters
            using (var cmdFilters = _connection.CreateCommand())
            {
                cmdFilters.CommandText = $@"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                           operator, value_type, value_json, value_source, user_attribute,
                                           filter_type, dependent_table, dependent_table_alias, foreign_key_column,
                                           primary_key_column, subquery_predicate_json, target_temporal_column,
                                           dependent_valid_from_column, dependent_valid_to_column, target_table_alias,
                                           additional_hops_json
                                    FROM CONSENT_ROW_FILTERS WHERE consent_id IN ({idList})";
                for (int i = 0; i < consentChunk.Length; i++)
                {
                    cmdFilters.Parameters.AddWithValue($"@cid{i}", consentChunk[i].Id.ToString());
                }

                var filtersMap = consentChunk.ToDictionary(c => c.Id, _ => new List<ConsentRowFilter>());
                using var reader = await cmdFilters.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var cid = Guid.Parse(reader.GetString(1));
                    if (!filtersMap.TryGetValue(cid, out var list)) continue;

                    TableIdentifier? depTable = null;
                    if (!reader.IsDBNull(11))
                    {
                        var depStr = reader.GetString(11);
                        if (TableIdentifier.TryParse(depStr, out var parsed))
                        {
                            depTable = parsed;
                        }
                    }

                    IReadOnlyList<SubqueryJoinHop>? hops = null;
                    if (!reader.IsDBNull(20))
                    {
                        var hopsJson = reader.GetString(20);
                        if (!string.IsNullOrWhiteSpace(hopsJson))
                        {
                            try
                            {
                                hops = System.Text.Json.JsonSerializer.Deserialize<List<SubqueryJoinHop>>(hopsJson);
                            }
                            catch (System.Text.Json.JsonException ex)
                            {
                                throw new InvalidOperationException("Malformed additional hops JSON detected in consent row filter.", ex);
                            }
                        }
                    }

                    list.Add(new ConsentRowFilter
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        ConsentId = cid,
                        FilterGroup = reader.GetInt32(2),
                        TableColumnId = Guid.Parse(reader.GetString(3)),
                        ColumnName = reader.GetString(4),
                        Operator = reader.GetString(5),
                        ValueType = reader.GetString(6),
                        ValueJson = reader.GetString(7),
                        ValueSource = reader.GetString(8),
                        UserAttribute = reader.IsDBNull(9) ? null : reader.GetString(9),
                        FilterType = (RowFilterType)(reader.IsDBNull(10) ? 0 : reader.GetInt32(10)),
                        DependentTable = depTable,
                        DependentTableAlias = reader.IsDBNull(12) ? null : reader.GetString(12),
                        ForeignKeyColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
                        PrimaryKeyColumn = reader.IsDBNull(14) ? null : reader.GetString(14),
                        SubqueryFilterPredicateJson = reader.IsDBNull(15) ? null : reader.GetString(15),
                        TargetTemporalColumn = reader.IsDBNull(16) ? null : reader.GetString(16),
                        DependentValidFromColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
                        DependentValidToColumn = reader.IsDBNull(18) ? null : reader.GetString(18),
                        TargetTableAlias = reader.IsDBNull(19) ? null : reader.GetString(19),
                        AdditionalHops = hops
                    });
                }

                foreach (var c in consentChunk)
                {
                    c.RowFilters = filtersMap[c.Id];
                }
            }
        }
    }


    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"INSERT INTO CONSENT_REQUESTS (id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref, business_justification, status, requested_at, requested_valid_to, itsm_ticket_id, tenant_id)
                                VALUES (@id, @tid, @req, @type, @ref, @just, @stat, @at, @to, @ticketId, @tenantId)";
            cmd.Parameters.AddWithValue("@id", request.Id.ToString());
            cmd.Parameters.AddWithValue("@tid", request.TableId.ToString());
            cmd.Parameters.AddWithValue("@req", request.RequesterSid.Value);
            cmd.Parameters.AddWithValue("@type", request.RequestedGranteeType.ToString());
            cmd.Parameters.AddWithValue("@ref", request.RequestedGranteeRef);
            cmd.Parameters.AddWithValue("@just", request.BusinessJustification);
            cmd.Parameters.AddWithValue("@stat", request.Status);
            cmd.Parameters.AddWithValue("@at", request.RequestedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@to", request.RequestedValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@ticketId", (object?)request.ItsmTicketId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tenantId", request.TenantId.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            return request;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                       r.itsm_ticket_id, r.tenant_id
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                WHERE r.id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
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
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(13))
                };
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
        => GetConsentRequestByTicketIdCoreAsync(ticketId, null, ct);

    /// <summary>
    /// SEC H-06: Tenant-bound ticket lookup – a ticket id of another tenant never resolves.
    /// </summary>
    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
        => GetConsentRequestByTicketIdCoreAsync(ticketId, tenantId, ct);

    private async Task<ConsentRequest?> GetConsentRequestByTicketIdCoreAsync(string ticketId, TenantId? tenantId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                       r.itsm_ticket_id, r.tenant_id
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                WHERE r.itsm_ticket_id = @ticketId";
            cmd.Parameters.AddWithValue("@ticketId", ticketId);
            if (tenantId.HasValue)
            {
                cmd.CommandText += " AND r.tenant_id = @tenantId";
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
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
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(13))
                };
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, null, ct);

    public async Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            ConsentRequest? req = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                           r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                           COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                           r.tenant_id
                                    FROM CONSENT_REQUESTS r
                                    JOIN TABLES t ON r.table_id = t.id
                                    LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                    WHERE r.id = @id";
                cmd.Parameters.AddWithValue("@id", requestId.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    req = new ConsentRequest
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
                        TenantId = reader.IsDBNull(12) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(12))
                    };
                }
            }

            if (req == null) return;

            using var tx = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                // SEC H-06: Only a pending or newly approved request may be activated.
                cmd.CommandText = @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                                    WHERE id = @id AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL', 'APPROVED')";
                cmd.Parameters.AddWithValue("@id", requestId.ToString());
                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
                if (rowsAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    return;
                }
            }

            using (var checkExistingCmd = _connection.CreateCommand())
            {
                checkExistingCmd.Transaction = tx;
                checkExistingCmd.CommandText = "SELECT COUNT(1) FROM CONSENTS WHERE consent_request_id = @reqId AND is_revoked = 0";
                checkExistingCmd.Parameters.AddWithValue("@reqId", req.Id.ToString());
                var existingCount = Convert.ToInt32(await checkExistingCmd.ExecuteScalarAsync(ct));
                if (existingCount > 0)
                {
                    await tx.RollbackAsync(ct);
                    return;
                }
            }

            var isRole = req.RequestedGranteeType == GranteeType.Role;
            var consentId = Guid.NewGuid();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                    VALUES (@id, @tid, @reqId, 'Allow', @type, @sid, @roleId, @roleName, @from, @to, 0, @tenantId)";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                cmd.Parameters.AddWithValue("@reqId", req.Id.ToString());
                cmd.Parameters.AddWithValue("@type", req.RequestedGranteeType.ToString());
                cmd.Parameters.AddWithValue("@sid", isRole ? DBNull.Value : (object)req.RequestedGranteeRef);
                cmd.Parameters.AddWithValue("@roleId", DBNull.Value);
                cmd.Parameters.AddWithValue("@roleName", isRole ? (object)req.RequestedGranteeRef : DBNull.Value);
                cmd.Parameters.AddWithValue("@from", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@to", req.RequestedValidTo.ToString("O"));
                cmd.Parameters.AddWithValue("@tenantId", req.TenantId.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await IncrementTableEpochInternalAsync(req.TableIdentifier, tx, ct);
            await tx.CommitAsync(ct);

            var auditEntry = new AuditLogEntry
            {
                TenantId = req.TenantId,
                EventType = "CONSENT_GRANTED",
                ActorSid = approvedBy ?? req.RequesterSid,
                TargetTable = req.TableIdentifier.ToString(),
                Decision = "APPROVED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consentId,
                    RequestId = req.Id,
                    GranteeType = req.RequestedGranteeType.ToString(),
                    GranteeRef = req.RequestedGranteeRef,
                    ValidTo = req.RequestedValidTo
                })
            };
            await RecordAuditEventInternalAsync(auditEntry, ct);

            await _epochValidationService.InvalidateEpochAsync(req.TableIdentifier, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM CONSENT_REQUESTS WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE CONSENT_REQUESTS SET itsm_ticket_id = @ticketId WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());
            cmd.Parameters.AddWithValue("@ticketId", ticketId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = new List<ConsentRequest>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL')
                                  AND r.table_id IN (
                                      SELECT tow.table_id
                                      FROM TABLE_OWNERS tow
                                      JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
                                      WHERE o.ad_sid = @apprSid AND o.is_active = 1
                                      UNION
                                      SELECT tow.table_id
                                      FROM DATA_OWNER_DELEGATIONS del
                                      JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
                                      WHERE del.delegate_sid = @apprSid AND del.valid_from <= @now AND @now < del.valid_to
                                  )";
            cmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var req = new ConsentRequest
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
                    TableIdentifier = new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11))
                };
                list.Add(req);
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!string.Equals(req.Status, "PENDING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Request {requestId} is in status '{req.Status}' and cannot be approved.");
        }

        if (req.RequesterSid == approverSid || approverSid.Value.EndsWith(req.RequesterSid.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Funktionstrennung verletzt: Antragsteller darf eigenen Antrag nicht genehmigen.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            string currentStatus;
            using (var checkCmd = _connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";
                checkCmd.Parameters.AddWithValue("@id", requestId.ToString());
                var statusObj = await checkCmd.ExecuteScalarAsync(ct);
                if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
                currentStatus = statusObj.ToString()!;
            }

            if (!string.Equals(currentStatus, "PENDING", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentStatus, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentStatus, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Request {requestId} is in status '{currentStatus}' and cannot be approved.");
            }

            // Four-eyes principle / Separation of duties check at repository layer
            if (req.RequesterSid == approverSid || approverSid.Value.EndsWith(req.RequesterSid.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Funktionstrennung verletzt: Der Antragsteller kann den eigenen Consent-Antrag nicht genehmigen.");
            }

            bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new UnauthorizedAccessException($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für Tabelle '{req.TableIdentifier}'.");
            }

            // Check if table requires four-eyes
            bool requiresFourEyes = false;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT requires_four_eyes FROM TABLES WHERE id = @tid";
                cmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                var obj = await cmd.ExecuteScalarAsync(ct);
                if (obj != null && obj != DBNull.Value)
                {
                    requiresFourEyes = Convert.ToInt32(obj) == 1;
                }
            }

            // Check existing approval steps
            var existingApprovers = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT approver_sid FROM APPROVAL_STEPS WHERE consent_request_id = @reqId AND decision = 'APPROVED' ORDER BY step_number";
                cmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    existingApprovers.Add(reader.GetString(0));
                }
            }

            if (existingApprovers.Any(s => string.Equals(s, approverSid.Value, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Vier-Augen-Prinzip verletzt: Genehmiger hat diesen Antrag bereits genehmigt.");
            }

            int nextStep = existingApprovers.Count + 1;
            string newStatus = (requiresFourEyes && nextStep < 2) ? "PENDING_SECOND_APPROVAL" : "APPROVED";

            using var tx = _connection.BeginTransaction();
            // Insert approval step
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, decided_at)
                                    VALUES (@id, @reqId, @step, @appr, 'APPROVED', @now)";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                cmd.Parameters.AddWithValue("@step", nextStep);
                cmd.Parameters.AddWithValue("@appr", approverSid.Value);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Update status
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE CONSENT_REQUESTS SET status = @st WHERE id = @id";
                cmd.Parameters.AddWithValue("@st", newStatus);
                cmd.Parameters.AddWithValue("@id", requestId.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            req.Status = newStatus;
            return req;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!string.Equals(req.Status, "PENDING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Request {requestId} is in status '{req.Status}' and cannot be rejected.");
        }

        await _lock.WaitAsync(ct);
        try
        {
            string currentStatus;
            using (var checkCmd = _connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";
                checkCmd.Parameters.AddWithValue("@id", requestId.ToString());
                var statusObj = await checkCmd.ExecuteScalarAsync(ct);
                if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
                currentStatus = statusObj.ToString()!;
            }

            if (!string.Equals(currentStatus, "PENDING", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentStatus, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(currentStatus, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Request {requestId} is in status '{currentStatus}' and cannot be rejected.");
            }

            bool isAuthorized = string.Equals(approverSid.Value, "ITSM_SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                                approverSid.Value.StartsWith("ITSM_", StringComparison.OrdinalIgnoreCase) ||
                                await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new UnauthorizedAccessException($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für Tabelle '{req.TableIdentifier}'.");
            }

            using var tx = _connection.BeginTransaction();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, rejection_reason, decided_at)
                                    VALUES (@id, @reqId, (SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @reqId), @appr, 'REJECTED', @reason, @now)";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                cmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                cmd.Parameters.AddWithValue("@appr", approverSid.Value);
                cmd.Parameters.AddWithValue("@reason", reason);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE CONSENT_REQUESTS SET status = 'REJECTED' WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", requestId.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            req.Status = "REJECTED";
            return req;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        consent.Validate();
        await _lock.WaitAsync(ct);
        try
        {
            using var tx = _connection.BeginTransaction();

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                VALUES (@id, @tid, @reqId, @effect, @type, @sid, @roleId, @roleName, @from, @to, 0, @tenantId)";
            cmd.Parameters.AddWithValue("@id", consent.Id.ToString());
            cmd.Parameters.AddWithValue("@tid", consent.TableId.ToString());
            cmd.Parameters.AddWithValue("@reqId", (object?)consent.ConsentRequestId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@effect", consent.Effect.ToString());
            cmd.Parameters.AddWithValue("@type", consent.GranteeType.ToString());
            cmd.Parameters.AddWithValue("@sid", (object?)consent.GranteeSid?.Value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@roleId", (object?)consent.RoleId?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@roleName", (object?)consent.RoleName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@from", consent.ValidFrom.ToString("O"));
            cmd.Parameters.AddWithValue("@to", consent.ValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@tenantId", (object?)consent.TenantId.Value ?? TenantId.LegacySingleTenant.Value);

            await cmd.ExecuteNonQueryAsync(ct);

            foreach (var rule in consent.ColumnRules)
            {
                using var ruleCmd = _connection.CreateCommand();
                ruleCmd.Transaction = tx;
                ruleCmd.CommandText = @"INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                                        VALUES (@id, @cid, @tcid, @col, @lvl)";
                ruleCmd.Parameters.AddWithValue("@id", rule.Id.ToString());
                ruleCmd.Parameters.AddWithValue("@cid", consent.Id.ToString());
                ruleCmd.Parameters.AddWithValue("@tcid", rule.TableColumnId.ToString());
                ruleCmd.Parameters.AddWithValue("@col", rule.ColumnName);
                ruleCmd.Parameters.AddWithValue("@lvl", (int)rule.AccessLevel);
                await ruleCmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var filter in consent.RowFilters)
            {
                using var filterCmd = _connection.CreateCommand();
                filterCmd.Transaction = tx;
                filterCmd.CommandText = @"INSERT INTO CONSENT_ROW_FILTERS (
                    id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                    filter_type, dependent_table, dependent_table_alias, foreign_key_column, primary_key_column,
                    subquery_predicate_json, target_temporal_column, dependent_valid_from_column, dependent_valid_to_column,
                    target_table_alias, additional_hops_json)
                VALUES (
                    @id, @cid, @grp, @tcid, @col, @op, @type, @json, @src, @attr,
                    @ftype, @deptbl, @depalias, @fkcol, @pkcol,
                    @subpred, @ttemp, @dvfrom, @dvto,
                    @talias, @hops)";
                filterCmd.Parameters.AddWithValue("@id", filter.Id.ToString());
                filterCmd.Parameters.AddWithValue("@cid", consent.Id.ToString());
                filterCmd.Parameters.AddWithValue("@grp", filter.FilterGroup);
                filterCmd.Parameters.AddWithValue("@tcid", filter.TableColumnId.ToString());
                filterCmd.Parameters.AddWithValue("@col", filter.ColumnName);
                filterCmd.Parameters.AddWithValue("@op", filter.Operator);
                filterCmd.Parameters.AddWithValue("@type", filter.ValueType);
                filterCmd.Parameters.AddWithValue("@json", filter.ValueJson);
                filterCmd.Parameters.AddWithValue("@src", filter.ValueSource);
                filterCmd.Parameters.AddWithValue("@attr", (object?)filter.UserAttribute ?? DBNull.Value);

                filterCmd.Parameters.AddWithValue("@ftype", (int)filter.FilterType);
                filterCmd.Parameters.AddWithValue("@deptbl", filter.DependentTable.HasValue ? (object)filter.DependentTable.Value.ToString() : DBNull.Value);
                filterCmd.Parameters.AddWithValue("@depalias", (object?)filter.DependentTableAlias ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@fkcol", (object?)filter.ForeignKeyColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@pkcol", (object?)filter.PrimaryKeyColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@subpred", (object?)filter.SubqueryFilterPredicateJson ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@ttemp", (object?)filter.TargetTemporalColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@dvfrom", (object?)filter.DependentValidFromColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@dvto", (object?)filter.DependentValidToColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@talias", (object?)filter.TargetTableAlias ?? DBNull.Value);
                var hopsJson = filter.AdditionalHops != null ? System.Text.Json.JsonSerializer.Serialize(filter.AdditionalHops) : null;
                filterCmd.Parameters.AddWithValue("@hops", (object?)hopsJson ?? DBNull.Value);

                await filterCmd.ExecuteNonQueryAsync(ct);
            }

            await IncrementTableEpochInternalAsync(consent.TableIdentifier, tx, ct);
            await tx.CommitAsync(ct);

            var auditEntry = new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = "CONSENT_GRANTED",
                ActorSid = consent.GranteeSid ?? new Sid("SYSTEM"),
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "GRANTED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consent.Id,
                    GranteeType = consent.GranteeType.ToString(),
                    GranteeSid = consent.GranteeSid?.Value,
                    ValidFrom = consent.ValidFrom,
                    ValidTo = consent.ValidTo
                })
            };
            await RecordAuditEventInternalAsync(auditEntry, ct);

            // Invalidate cache after successful commit
            await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct);

            return consent;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Consent? consent = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                           t.source_name, t.schema_name, t.table_name, c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var gTypeStr = reader.GetString(4);
                    var granteeType = Enum.Parse<GranteeType>(gTypeStr, true);
                    var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
                    var domain = reader.GetString(14);
                    var schema = reader.GetString(15);
                    var tableName = reader.GetString(16);

                    consent = new Consent
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = Guid.Parse(reader.GetString(1)),
                        TableIdentifier = new TableIdentifier(domain, schema, tableName),
                        ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                        GranteeType = granteeType,
                        GranteeSid = granteeSidStr != null ? new Sid(granteeSidStr) : (Sid?)null,
                        RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                        RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        ValidFrom = DateTimeOffset.Parse(reader.GetString(8)),
                        ValidTo = DateTimeOffset.Parse(reader.GetString(9)),
                        IsRevoked = reader.GetInt32(10) == 1,
                        RevokedBySid = reader.IsDBNull(11) ? (Sid?)null : new Sid(reader.GetString(11)),
                        RevokedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
                        RevokeReason = reader.IsDBNull(13) ? null : reader.GetString(13),
                        TenantId = reader.IsDBNull(17) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(17))
                    };
                }
            }

            if (consent != null)
            {
                consent.ColumnRules = await LoadColumnRulesAsync(consent.Id, ct);
                consent.RowFilters = await LoadRowFiltersAsync(consent.Id, ct);
            }

            return consent;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            TableIdentifier? tableId = null;
            bool isGranteeSelf = false;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name, c.grantee_type, c.grantee_sid
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                    WHERE c.id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    var gType = reader.GetString(3);
                    var gSid = reader.IsDBNull(4) ? null : reader.GetString(4);
                    if (string.Equals(gType, "User", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(gSid, revokedBySid.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        isGranteeSelf = true;
                    }
                }
            }

            if (!tableId.HasValue)
            {
                throw new KeyNotFoundException($"Consent mit ID '{consentId}' existiert nicht.");
            }

            if (!isGranteeSelf)
            {
                bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(tableId.Value, revokedBySid, ct);
                if (!isAuthorized)
                {
                    throw new UnauthorizedAccessException($"Benutzer '{revokedBySid}' ist weder Data Owner oder delegierter Genehmiger für '{tableId.Value}', noch der Begünstigte selbst.");
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"UPDATE CONSENTS
                                    SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                    WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@by", revokedBySid.Value);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@reason", reason);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(tableId.Value, ct);
            }

            var auditEntry = new AuditLogEntry
            {
                EventType = "CONSENT_REVOKED",
                ActorSid = revokedBySid,
                TargetTable = tableId?.ToString() ?? "UNKNOWN",
                Decision = "REVOKED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    ConsentId = consentId,
                    Reason = reason
                })
            };
            await RecordAuditEventInternalAsync(auditEntry, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var consents = new List<Consent>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                       c.tenant_id, t.source_name, t.schema_name, t.table_name
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.is_revoked = 0
                                  AND c.valid_to > @now
                                  AND c.valid_to <= @threshold";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@threshold", threshold.ToString("O"));

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = Guid.Parse(reader.GetString(0));
                var tableId = Guid.Parse(reader.GetString(1));
                Guid? requestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
                var effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true);
                var granteeType = Enum.Parse<GranteeType>(reader.GetString(4), true);
                Sid? granteeSid = reader.IsDBNull(5) ? (Sid?)null : new Sid(reader.GetString(5));
                Guid? roleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6));
                var roleName = reader.IsDBNull(7) ? null : reader.GetString(7);
                var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);
                var isRevoked = reader.GetInt32(10) == 1;
                var tenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14));
                var domain = reader.GetString(15);
                var schema = reader.GetString(16);
                var tableName = reader.GetString(17);

                consents.Add(new Consent
                {
                    Id = id,
                    TableId = tableId,
                    TableIdentifier = new TableIdentifier(domain, schema, tableName),
                    ConsentRequestId = requestId,
                    Effect = effect,
                    GranteeType = granteeType,
                    GranteeSid = granteeSid,
                    RoleId = roleId,
                    RoleName = roleName,
                    ValidFrom = validFrom,
                    ValidTo = validTo,
                    IsRevoked = isRevoked,
                    TenantId = tenantId
                });
            }
            return consents;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            TableIdentifier? tableId = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"UPDATE CONSENTS
                                    SET valid_to = @validTo
                                    WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@validTo", newValidTo.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(tableId.Value, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

