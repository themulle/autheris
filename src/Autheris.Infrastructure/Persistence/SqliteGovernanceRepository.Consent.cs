using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Governance.ConsentRequests;
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
    /// <summary>
    /// Test seam (WP-0, SEC-113-R2-03): runs inside the decision transaction between the status read under the lock and the guarded
    /// status UPDATE, so a test can change the status where no concurrent writer can be injected. Always null in production.
    /// </summary>
    internal Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam { get; set; }

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
        var subjectList = subjects.ToList();
        var subjectSet = subjectList.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0) return Array.Empty<Consent>();
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectList);

        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var byId = new Dictionary<Guid, Consent>();
            foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = BuildSubjectConsentSql(chunk.Count, tenantId != null);
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);
                if (tenantId != null)
                {
                    cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
                }

                for (var i = 0; i < chunk.Count; i++)
                {
                    cmd.Parameters.AddWithValue("@s" + i, chunk[i]);
                }

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                    var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);

                    if (atTime >= validTo) continue;

                    var gEffectStr = reader.GetString(3);
                    var isDeny = Enum.TryParse<ConsentEffect>(gEffectStr, true, out var parsedEffect) && parsedEffect == ConsentEffect.Deny;
                    if (atTime < validFrom && !isDeny) continue;

                    var gTypeStr = reader.GetString(4);
                    var granteeType = Enum.Parse<GranteeType>(gTypeStr, true);
                    var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                    if (granteeType != GranteeType.Role && (granteeSidStr == null || !subjectSet.Contains(granteeSidStr)))
                    {
                        continue;
                    }

                    var id = Guid.Parse(reader.GetString(0));
                    if (byId.ContainsKey(id)) continue;

                    byId[id] = new Consent
                    {
                        Id = id,
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
                }
            }

            var consents = byId.Values.ToList();

            consents.RemoveAll(c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime)); // read-time clamp (WP-SC-16): never trust the raw valid_to
            // Hydrate column rules & row filters in batch (K-01)
            await HydrateConsentsBatchAsync(conn, consents, ct);

            return consents;
        }
    }

    /// <summary>
    /// WP-SC-01: the indexed subject/table consent query. The filter on <c>grantee_sid_norm</c> is a prefilter only (role consents and
    /// rows without a normalised SID always pass); the C# filter on the loaded rows stays authoritative.
    /// </summary>
    internal static string BuildSubjectConsentSql(int subjectCount, bool withTenant)
    {
        var tenantFilter = withTenant ? " AND c.tenant_id = @tenantId" : "";
        return @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
                                      AND c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(subjectCount);
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
        var subjectEnumerated = subjects.ToList();
        var subjectList = subjectEnumerated.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectEnumerated);
        var roleSet = roles != null
            ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var byId = new Dictionary<Guid, Consent>();
            foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = BuildAllConsentsSql(chunk.Count, tenantId != null);
                if (tenantId != null)
                {
                    cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
                }

                for (var i = 0; i < chunk.Count; i++)
                {
                    cmd.Parameters.AddWithValue("@s" + i, chunk[i]);
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

                    var id = Guid.Parse(reader.GetString(0));
                    if (byId.ContainsKey(id)) continue;

                    var domain = reader.GetString(11);
                    var tableIdentifier = new TableIdentifier(domain, reader.GetString(12), reader.GetString(13));

                    byId[id] = new Consent
                    {
                        Id = id,
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
                }
            }

            var consents = byId.Values.ToList();

            consents.RemoveAll(c => !c.IsActive(effectiveAt)); // read-time clamp (WP-SC-16): never trust the raw valid_to
            // Hydrate column rules & row filters in batch (K-01)
            await HydrateConsentsBatchAsync(conn, consents, ct);

            return consents;
        }
    }

    internal static string BuildAllConsentsSql(int subjectCount, bool withTenant)
    {
        var tenantFilter = withTenant ? " AND c.tenant_id = @tenantId" : "";
        return @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(subjectCount);
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var consents = new List<Consent>();
            using (var cmd = conn.CreateCommand())
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
    }

    /// <summary>
    /// SEC E-05 / EX-02: Revokes a sync-owned consent (consent_request_id = marker) without data-owner check. Manual or
    /// workflow consents never match the marker and therefore cannot be revoked through this path.
    /// </summary>
    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            TableIdentifier? tableId = null;
            TenantId tenantId = TenantId.LegacySingleTenant;
            using (var cmd = conn.CreateCommand())
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

            using (var cmd = conn.CreateCommand())
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

            await IncrementTableEpochInternalAsync(conn, tableId.Value, ct);

            await RecordAuditEventInternalAsync(conn, new AuditLogEntry
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
    }

    private async Task<IReadOnlyList<ConsentColumnRule>> LoadColumnRulesAsync(SqliteConnection conn, Guid consentId, CancellationToken ct)
    {
        var rules = new List<ConsentColumnRule>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, consent_id, table_column_id, column_name, access_level, applicable_scopes
                            FROM CONSENT_COLUMN_RULES WHERE consent_id = @cid";
        cmd.Parameters.AddWithValue("@cid", consentId.ToString());

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rawLevel = reader.GetInt32(4);
            var scopesStr = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : string.Empty;
            var scopes = string.IsNullOrWhiteSpace(scopesStr)
                ? Array.Empty<string>()
                : scopesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rules.Add(new ConsentColumnRule
            {
                Id = Guid.Parse(reader.GetString(0)),
                ConsentId = Guid.Parse(reader.GetString(1)),
                TableColumnId = Guid.Parse(reader.GetString(2)),
                ColumnName = reader.GetString(3),
                AccessLevel = Enum.IsDefined((ColumnAccessLevel)rawLevel)
                    ? (ColumnAccessLevel)rawLevel
                    : ColumnAccessLevel.Deny,
                ApplicableScopes = scopes
            });
        }
        return rules;
    }

    private async Task<IReadOnlyList<ConsentRowFilter>> LoadRowFiltersAsync(SqliteConnection conn, Guid consentId, CancellationToken ct)
    {
        var filters = new List<ConsentRowFilter>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                   operator, value_type, value_json, value_source, user_attribute,
                                   filter_type, dependent_table, dependent_table_alias, foreign_key_column,
                                   primary_key_column, subquery_predicate_json, target_temporal_column,
                                   dependent_valid_from_column, dependent_valid_to_column, target_table_alias,
                                   additional_hops_json, key_set_strategy, key_set_duration_seconds, key_set_max_keys
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
                AdditionalHops = hops,
                KeySetStrategy = reader.IsDBNull(21) ? null : (RowFilterEvaluationStrategy)reader.GetInt32(21),
                KeySetDurationSeconds = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                KeySetMaxKeys = reader.IsDBNull(23) ? null : reader.GetInt32(23)
            });
        }
        return filters;
    }

    /// <summary>PLAN-GOV-158-INC1: opt-in load of <c>CONSENTS.description</c> (<see cref="IConsentDescriptionRepository.LoadDescriptionsAsync"/>); never part of hydration.</summary>
    public async Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consents);
        if (consents.Count == 0) return;
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        await LoadConsentDescriptionsAsync(scope.Connection, consents, ct);
    }

    private static async Task LoadConsentDescriptionsAsync(SqliteConnection conn, IReadOnlyCollection<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;
        var byId = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in consents)
        {
            if (!byId.TryGetValue(c.Id.ToString(), out var list)) byId[c.Id.ToString()] = list = [];
            list.Add(c);
        }

        foreach (var chunk in byId.Keys.Chunk(500))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id, description FROM CONSENTS WHERE description IS NOT NULL AND id IN ({string.Join(",", chunk.Select((_, i) => $"@did{i}"))})";
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue($"@did{i}", chunk[i]);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!byId.TryGetValue(reader.GetString(0), out var targets)) continue;
                var text = reader.GetString(1);
                foreach (var c in targets) c.Description = text;
            }
        }
    }

    private async Task HydrateConsentsBatchAsync(SqliteConnection conn, List<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;

        if (consents.Count == 1)
        {
            var single = consents[0];
            single.ColumnRules = await LoadColumnRulesAsync(conn, single.Id, ct);
            single.RowFilters = await LoadRowFiltersAsync(conn, single.Id, ct);
            return;
        }

        // Chunk by 500 to protect SQLite 999 parameter limit
        foreach (var consentChunk in consents.Chunk(500))
        {
            var idList = string.Join(",", consentChunk.Select((_, i) => $"@cid{i}"));

            // 1. Batch load column rules
            using (var cmdRules = conn.CreateCommand())
            {
                cmdRules.CommandText = $@"SELECT id, consent_id, table_column_id, column_name, access_level, applicable_scopes
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
                        var rawLevel = reader.GetInt32(4);
                        var scopesStr = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : string.Empty;
                        var scopes = string.IsNullOrWhiteSpace(scopesStr)
                            ? Array.Empty<string>()
                            : scopesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        list.Add(new ConsentColumnRule
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            ConsentId = cid,
                            TableColumnId = Guid.Parse(reader.GetString(2)),
                            ColumnName = reader.GetString(3),
                            AccessLevel = Enum.IsDefined((ColumnAccessLevel)rawLevel)
                                ? (ColumnAccessLevel)rawLevel
                                : ColumnAccessLevel.Deny,
                            ApplicableScopes = scopes
                        });
                    }
                }

                foreach (var c in consentChunk)
                {
                    c.ColumnRules = rulesMap[c.Id];
                }
            }

            // 2. Batch load row filters
            using (var cmdFilters = conn.CreateCommand())
            {
                cmdFilters.CommandText = $@"SELECT id, consent_id, filter_group, table_column_id, column_name,
                                           operator, value_type, value_json, value_source, user_attribute,
                                           filter_type, dependent_table, dependent_table_alias, foreign_key_column,
                                           primary_key_column, subquery_predicate_json, target_temporal_column,
                                           dependent_valid_from_column, dependent_valid_to_column, target_table_alias,
                                           additional_hops_json, key_set_strategy, key_set_duration_seconds, key_set_max_keys
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
                        AdditionalHops = hops,
                        KeySetStrategy = reader.IsDBNull(21) ? null : (RowFilterEvaluationStrategy)reader.GetInt32(21),
                        KeySetDurationSeconds = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        KeySetMaxKeys = reader.IsDBNull(23) ? null : reader.GetInt32(23)
                    });
                }

                foreach (var c in consentChunk)
                {
                    c.RowFilters = filtersMap[c.Id];
                }
            }
        }
    }


    public async Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(
        Guid rowFilterId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        var row = await RowFilterEvaluationStore.ReadRuleAsync(scope.Connection, null, CatalogKeyDefinitionStore.Sqlite, rowFilterId, ct).ConfigureAwait(false);
        return row is null ? null : (row.Filter, row.Effect, row.Tenant, row.Table);
    }

    public async Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
    {
        RowFilterEvaluationChange? change;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
            try
            {
                change = await RowFilterEvaluationStore.UpdateRuleAsync(
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, rowFilterId, tenant, settings,
                    table => BumpTableEpochAndReadAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        QueueMetadataInvalidation(change.TargetTable);
        await _epochValidationService.InvalidateEpochAsync(change.TargetTable, ct);
        return change;
    }

    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            var reqIdsJson = request.RequesterIdentifiers.Count > 0
                ? JsonSerializer.Serialize(request.RequesterIdentifiers)
                : null;
            cmd.CommandText = @"INSERT INTO CONSENT_REQUESTS (id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref, business_justification, status, requested_at, requested_valid_to, itsm_ticket_id, tenant_id, requester_identifiers_json)
                                VALUES (@id, @tid, @req, @type, @ref, @just, @stat, @at, @to, @ticketId, @tenantId, @reqIds)";
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
            cmd.Parameters.AddWithValue("@reqIds", (object?)reqIdsJson ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            return request;
        }
    }

    public async Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                       r.itsm_ticket_id, r.tenant_id, r.requester_identifiers_json
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                                WHERE r.id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var reqIdsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                var reqIds = !string.IsNullOrWhiteSpace(reqIdsJson)
                    ? (JsonSerializer.Deserialize<List<string>>(reqIdsJson) ?? new List<string>())
                    : new List<string>();

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
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(13)),
                    RequesterIdentifiers = reqIds
                };
            }
            return null;
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
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                       r.itsm_ticket_id, r.tenant_id, r.requester_identifiers_json
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
                var reqIdsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                var reqIds = !string.IsNullOrWhiteSpace(reqIdsJson)
                    ? (JsonSerializer.Deserialize<List<string>>(reqIdsJson) ?? new List<string>())
                    : new List<string>();

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
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(13)),
                    RequesterIdentifiers = reqIds
                };
            }
            return null;
        }
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    /// <summary>Insecure getting-started auto-approve path only: activates a request that is still pending.</summary>
    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private async Task ActivateConsentCoreAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            ConsentRequest? req = null;
            using (var cmd = conn.CreateCommand())
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

            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                // SEC H-06: Only a pending or newly approved request may be activated.
                // Review G5: regular activation only from 'APPROVED' (i.e. after the approval steps); pending states only for dev auto-approve.
                cmd.CommandText = allowPendingAutoApprove
                    ? @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                        WHERE id = @id AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL', 'APPROVED')"
                    : @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                        WHERE id = @id AND status = 'APPROVED'";
                cmd.Parameters.AddWithValue("@id", requestId.ToString());
                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
                if (rowsAffected != 1)
                {
                    await tx.RollbackAsync(ct);
                    return;
                }
            }

            using (var checkExistingCmd = conn.CreateCommand())
            {
                checkExistingCmd.Transaction = tx;
                // SEC N-2: Disallow multiple activations of the same consent_request_id, regardless of revocation status.
                checkExistingCmd.CommandText = "SELECT COUNT(1) FROM CONSENTS WHERE consent_request_id = @reqId";
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                    VALUES (@id, @tid, @reqId, 'Allow', @type, @sid, @sidNorm, @roleId, @roleName, @from, @to, 0, @tenantId)";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                cmd.Parameters.AddWithValue("@reqId", req.Id.ToString());
                cmd.Parameters.AddWithValue("@type", req.RequestedGranteeType.ToString());
                cmd.Parameters.AddWithValue("@sid", isRole ? DBNull.Value : (object)req.RequestedGranteeRef);
                cmd.Parameters.AddWithValue("@sidNorm", ConsentSubjectFilter.NormalizeParameter(isRole ? DBNull.Value : (object)req.RequestedGranteeRef));
                cmd.Parameters.AddWithValue("@roleId", DBNull.Value);
                cmd.Parameters.AddWithValue("@roleName", isRole ? (object)req.RequestedGranteeRef : DBNull.Value);
                cmd.Parameters.AddWithValue("@from", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@to", req.RequestedValidTo.ToString("O"));
                cmd.Parameters.AddWithValue("@tenantId", req.TenantId.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // POL-3: Freeze full column snapshot from table metadata on ITSM activation
            var columnRules = new List<(Guid Id, Guid ColId, string ColName, int AccessLevel)>();
            using (var colCmd = conn.CreateCommand())
            {
                colCmd.Transaction = tx;
                colCmd.CommandText = @"SELECT c.id, c.column_name, c.is_sensitive,
                                              MAX(CASE WHEN m.id IS NOT NULL THEN 1 ELSE 0 END) AS has_mask
                                       FROM TABLE_COLUMNS c
                                       LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                       WHERE c.table_id = @tid
                                       GROUP BY c.id, c.column_name, c.is_sensitive";
                colCmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                using var reader = await colCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
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
                using var ruleCmd = conn.CreateCommand();
                ruleCmd.Transaction = tx;
                ruleCmd.CommandText = @"INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level, applicable_scopes)
                                        VALUES (@id, @cid, @tcid, @col, @lvl, @scopes)";
                ruleCmd.Parameters.AddWithValue("@id", ruleId.ToString());
                ruleCmd.Parameters.AddWithValue("@cid", consentId.ToString());
                ruleCmd.Parameters.AddWithValue("@tcid", colId.ToString());
                ruleCmd.Parameters.AddWithValue("@col", colName);
                ruleCmd.Parameters.AddWithValue("@lvl", accessLevel);
                ruleCmd.Parameters.AddWithValue("@scopes", string.Empty);
                await ruleCmd.ExecuteNonQueryAsync(ct);
            }

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
            await RecordAuditEventInternalAsync(conn, auditEntry, ct, tx);

            await IncrementTableEpochInternalAsync(conn, req.TableIdentifier, tx, ct);
            await tx.CommitAsync(ct);
            OnTransactionCommitted(tx);

            await _epochValidationService.InvalidateEpochAsync(req.TableIdentifier, ct);
        }
    }

    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM CONSENT_REQUESTS WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE CONSENT_REQUESTS SET itsm_ticket_id = @ticketId WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());
            cmd.Parameters.AddWithValue("@ticketId", ticketId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var list = new List<ConsentRequest>();
            using var cmd = conn.CreateCommand();
            var sql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                               r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                               COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name
                        FROM CONSENT_REQUESTS r
                        JOIN TABLES t ON r.table_id = t.id
                        LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                        WHERE r.requester_sid = @reqSid";
            if (!string.IsNullOrWhiteSpace(status))
            {
                sql += " AND UPPER(r.status) = @status";
                cmd.Parameters.AddWithValue("@status", status.Trim().ToUpperInvariant());
            }
            sql += " ORDER BY r.requested_at DESC";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@reqSid", requesterSid.Value);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ConsentRequest
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
                });
            }
            return list;
        }
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var list = new List<ConsentRequest>();
            using var cmd = conn.CreateCommand();
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
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval: false, itsmApproverAccount: null, ct);

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount: null, ct);

    /// <summary>
    /// Approval step. Review E-9: a request waiting for the change board (<c>PENDING_EXTERNAL_APPROVAL</c>) can only be
    /// approved by the verified ITSM webhook, and an ITSM approval only applies to such requests.
    /// Review R3-1/R2-5: the approver reported by the ITSM system is passed typed (<paramref name="itsmApproverAccount"/>)
    /// and checked against owners, delegates and admins by account or e-mail; on tables with configured owners an
    /// ITSM approval without a named approver is rejected.
    /// </summary>
    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, isExternalItsmApproval);

        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            string currentStatus;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";
                checkCmd.Parameters.AddWithValue("@id", requestId.ToString());
                var statusObj = await checkCmd.ExecuteScalarAsync(ct);
                if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
                currentStatus = statusObj.ToString()!;
            }

            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval);

            // Four-eyes principle / Separation of duties check at repository layer
            if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
            {
                throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own consent request.");
            }

            // Review R4-4 (rest): compare by the stable data owner id as well, not only by spelling.
            var approverOwnerIds = await ResolveDataOwnerIdsInternalAsync(conn, ConsentApprovalPolicy.IdentifierCandidates(approverSid.Value, itsmApproverAccount), ct);
            if (ConsentApprovalPolicy.ShareOwnerId(approverOwnerIds, await ResolveRequesterOwnerIdsInternalAsync(conn, req, ct)))
            {
                throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own consent request.");
            }

            if (!isExternalItsmApproval)
            {
                bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, ct);
                if (!isAuthorized)
                {
                    throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
                }
            }
            else
            {
                // SEC N-1 / Review R3-1 + R2-5: tables with configured owners need a named ITSM approver who is an
                // owner, delegate or admin (matched by account or e-mail, passed typed, never parsed from the SID).
                bool hasConfiguredOwners = await HasConfiguredDataOwnersAsync(conn, req.TableIdentifier, ct);
                if (hasConfiguredOwners)
                {
                    string? account = string.IsNullOrWhiteSpace(itsmApproverAccount) ? null : itsmApproverAccount.Trim();
                    if (account == null)
                    {
                        throw new UnauthorizedAccessException($"ITSM approval without a named approver is not allowed for table '{req.TableIdentifier}' with configured data owners.");
                    }

                    bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, account, ct);
                    if (!isAuthorized)
                    {
                        throw new UnauthorizedAccessException($"ITSM approver '{account}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
                    }
                }
            }

            // Check if table requires four-eyes
            bool requiresFourEyes = false;
            string? tableSensitivity = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT requires_four_eyes, sensitivity FROM TABLES WHERE id = @tid";
                cmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    if (!reader.IsDBNull(0))
                    {
                        requiresFourEyes = Convert.ToInt32(reader.GetValue(0)) == 1;
                    }
                    if (!reader.IsDBNull(1))
                    {
                        tableSensitivity = reader.GetString(1);
                    }
                }
            }

            // Check existing approval steps
            var existingApprovers = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT approver_sid FROM APPROVAL_STEPS WHERE consent_request_id = @reqId AND decision = 'APPROVED' ORDER BY step_number";
                cmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    existingApprovers.Add(reader.GetString(0));
                }
            }

            bool sameApprover = existingApprovers.Any(s => ConsentApprovalPolicy.IsSameApprover(s, approverSid, itsmApproverAccount));
            for (int i = 0; !sameApprover && i < existingApprovers.Count; i++)
            {
                var storedOwnerIds = await ResolveDataOwnerIdsInternalAsync(conn, ConsentApprovalPolicy.IdentifierCandidates(existingApprovers[i]), ct);
                sameApprover = ConsentApprovalPolicy.ShareOwnerId(storedOwnerIds, approverOwnerIds);
            }

            if (sameApprover)
            {
                if (isExternalItsmApproval)
                {
                    // Redelivered ITSM webhook for a step that is already recorded: idempotent, nothing changes.
                    return req;
                }

                throw new InvalidOperationException("Vier-Augen-Prinzip verletzt: Genehmiger hat diesen Antrag bereits genehmigt.");
            }

            int nextStep = existingApprovers.Count + 1;
            // Review E-9: an ITSM-governed request stays with the change board for its second step as well, so the
            // four-eyes decision cannot be assembled from one ITSM and one GraphQL approval.
            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(requiresFourEyes, nextStep, isExternalItsmApproval, tableSensitivity);

            using var tx = conn.BeginTransaction();
            // Insert approval step
            using (var cmd = conn.CreateCommand())
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
            await ApplyGuardedStatusAsync(conn, tx, requestId, newStatus, currentStatus, ct);

            req.Status = newStatus;
            await RecordAuditEventInternalAsync(conn,
                ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, itsmApproverAccount, null), ct, tx);

            tx.Commit();
            OnTransactionCommitted(tx);
            return req;
        }
    }

    /// <summary>
    /// Status guard of the approve step and the reject path (WP-0): the UPDATE only matches while the request still has the status read under
    /// the write gate. Zero rows throw, the caller's transaction is disposed without commit (no step row, no audit entry, no status change).
    /// </summary>
    private async Task ApplyGuardedStatusAsync(SqliteConnection conn, SqliteTransaction tx, Guid requestId, string newStatus, string expectedStatus, CancellationToken ct)
    {
        if (ConsentStatusUpdateSeam is { } seam)
        {
            await seam(conn, tx, ct);
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = ConsentApprovalPolicy.GuardedStatusUpdateSql;
        cmd.Parameters.AddWithValue("@st", newStatus);
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        cmd.Parameters.AddWithValue("@expected", expectedStatus);
        try
        {
            ConsentApprovalPolicy.EnsureTransitionApplied(await cmd.ExecuteNonQueryAsync(ct), requestId);
        }
        catch (ConsentRequestStatusConflictException)
        {
            // Same hook as the PostgreSQL/SQL Server catch blocks: roll back first (not cancellable), then release the transaction's audit chain state.
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) =>
        RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm: false, ct);

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!string.Equals(req.Status, "PENDING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.Status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId); // lost the race: ACCESS_REQUEST_NOT_PENDING
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            string currentStatus;
            using (var checkCmd = conn.CreateCommand())
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
                throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId); // lost the race: ACCESS_REQUEST_NOT_PENDING
            }

            bool isAuthorized = isExternalItsm ||
                                await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, ct);
            if (!isAuthorized)
            {
                throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
            }

            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
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

            await ApplyGuardedStatusAsync(conn, tx, requestId, "REJECTED", currentStatus, ct);

            req.Status = "REJECTED";
            await RecordAuditEventInternalAsync(conn,
                ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, reason), ct, tx);

            tx.Commit();
            OnTransactionCommitted(tx);
            return req;
        }
    }

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        consent.Validate();
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            using var tx = conn.BeginTransaction();
            await InsertConsentCoreAsync(conn, tx, consent, ct);

            await IncrementTableEpochInternalAsync(conn, consent.TableIdentifier, tx, ct);
            await tx.CommitAsync(ct);
            OnTransactionCommitted(tx);

            // Invalidate cache after successful commit
            await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct);

            return consent;
        }
    }

    /// <summary>Inserts the consent row, its column rules and row filters and the grant audit entry inside <paramref name="tx"/>. Neither bumps the epoch nor commits.</summary>
    private async Task InsertConsentCoreAsync(SqliteConnection conn, SqliteTransaction tx, Consent consent, CancellationToken ct)
    {

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id, description)
                            VALUES (@id, @tid, @reqId, @effect, @type, @sid, @sidNorm, @roleId, @roleName, @from, @to, 0, @tenantId, @description)";
        cmd.Parameters.AddWithValue("@id", consent.Id.ToString());
        cmd.Parameters.AddWithValue("@tid", consent.TableId.ToString());
        cmd.Parameters.AddWithValue("@reqId", (object?)consent.ConsentRequestId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@effect", consent.Effect.ToString());
        cmd.Parameters.AddWithValue("@type", consent.GranteeType.ToString());
        cmd.Parameters.AddWithValue("@sid", (object?)consent.GranteeSid?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sidNorm", ConsentSubjectFilter.NormalizeParameter((object?)consent.GranteeSid?.Value ?? DBNull.Value));
        cmd.Parameters.AddWithValue("@roleId", (object?)consent.RoleId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@roleName", (object?)consent.RoleName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@from", consent.ValidFrom.ToString("O"));
        cmd.Parameters.AddWithValue("@to", consent.ValidTo.ToString("O"));
        cmd.Parameters.AddWithValue("@tenantId", (object?)consent.TenantId.Value ?? TenantId.LegacySingleTenant.Value);
        cmd.Parameters.AddWithValue("@description", (object?)consent.Description ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        foreach (var rule in consent.ColumnRules)
        {
            using var ruleCmd = conn.CreateCommand();
            ruleCmd.Transaction = tx;
            ruleCmd.CommandText = @"INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level, applicable_scopes)
                                    VALUES (@id, @cid, @tcid, @col, @lvl, @scopes)";
            ruleCmd.Parameters.AddWithValue("@id", rule.Id.ToString());
            ruleCmd.Parameters.AddWithValue("@cid", consent.Id.ToString());
            ruleCmd.Parameters.AddWithValue("@tcid", rule.TableColumnId.ToString());
            ruleCmd.Parameters.AddWithValue("@col", rule.ColumnName);
            ruleCmd.Parameters.AddWithValue("@lvl", (int)rule.AccessLevel);
            ruleCmd.Parameters.AddWithValue("@scopes", rule.ApplicableScopes != null && rule.ApplicableScopes.Count > 0 ? string.Join(",", rule.ApplicableScopes) : string.Empty);
            await ruleCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var filter in consent.RowFilters)
        {
            using var filterCmd = conn.CreateCommand();
            filterCmd.Transaction = tx;
            filterCmd.CommandText = @"INSERT INTO CONSENT_ROW_FILTERS (
                id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                filter_type, dependent_table, dependent_table_alias, foreign_key_column, primary_key_column,
                subquery_predicate_json, target_temporal_column, dependent_valid_from_column, dependent_valid_to_column,
                target_table_alias, additional_hops_json, key_set_strategy, key_set_duration_seconds, key_set_max_keys)
            VALUES (
                @id, @cid, @grp, @tcid, @col, @op, @type, @json, @src, @attr,
                @ftype, @deptbl, @depalias, @fkcol, @pkcol,
                @subpred, @ttemp, @dvfrom, @dvto,
                @talias, @hops, @ksstrategy, @ksduration, @ksmax)";
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
            filterCmd.Parameters.AddWithValue("@ksstrategy", filter.KeySetStrategy is { } ksStrategy ? (object)(int)ksStrategy : DBNull.Value);
            filterCmd.Parameters.AddWithValue("@ksduration", (object?)filter.KeySetDurationSeconds ?? DBNull.Value);
            filterCmd.Parameters.AddWithValue("@ksmax", (object?)filter.KeySetMaxKeys ?? DBNull.Value);

            await filterCmd.ExecuteNonQueryAsync(ct);
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
                ConsentId = consent.Id,
                GranteeType = consent.GranteeType.ToString(),
                GranteeSid = consent.GranteeSid?.Value,
                ValidFrom = consent.ValidFrom,
                ValidTo = consent.ValidTo
            })
        };
        await RecordAuditEventInternalAsync(conn, auditEntry, ct, tx);
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            Consent? consent = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                           t.source_name, t.schema_name, t.table_name, c.tenant_id, c.renewal_count
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
                        TenantId = reader.IsDBNull(17) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(17)),
                        RenewalCount = reader.GetInt32(18)
                    };
                }
            }

            if (consent != null)
            {
                consent.ColumnRules = await LoadColumnRulesAsync(conn, consent.Id, ct);
                consent.RowFilters = await LoadRowFiltersAsync(conn, consent.Id, ct);
            }

            return consent;
        }
    }

    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            TableIdentifier? tableId = null;
            bool isGranteeSelf = false;
            TenantId revokeTenant = TenantId.LegacySingleTenant;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name, c.grantee_type, c.grantee_sid, c.tenant_id
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
                    if (!reader.IsDBNull(5))
                    {
                        revokeTenant = new TenantId(reader.GetString(5));
                    }

                    if (string.Equals(gType, "User", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(gSid, revokedBySid.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        isGranteeSelf = true;
                    }
                }
            }

            if (!tableId.HasValue)
            {
                throw new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.");
            }

            if (!isGranteeSelf)
            {
                bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(conn, tableId.Value, revokedBySid, ct);
                if (!isAuthorized)
                {
                    throw new UnauthorizedAccessException($"User '{revokedBySid}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.");
                }
            }

            using (var cmd = conn.CreateCommand())
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
                await IncrementTableEpochInternalAsync(conn, tableId.Value, ct);
            }

            var auditEntry = new AuditLogEntry
            {
                TenantId = revokeTenant,
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
            await RecordAuditEventInternalAsync(conn, auditEntry, ct);
        }
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            var consents = new List<Consent>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                       c.tenant_id, t.source_name, t.schema_name, t.table_name, c.renewal_count
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
                    TenantId = tenantId,
                    RenewalCount = reader.GetInt32(18)
                });
            }
            return consents;
        }
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        {
            TableIdentifier? tableId = null;
            TenantId extendTenant = TenantId.LegacySingleTenant;
            var newValidFrom = DateTimeOffset.UtcNow;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.valid_from
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    newValidFrom = ConsentSubjectFilter.RenewalPeriodStart(reader.GetString(4), DateTimeOffset.UtcNow);
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    if (!reader.IsDBNull(3))
                    {
                        extendTenant = new TenantId(reader.GetString(3));
                    }
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE CONSENTS
                                    SET valid_to = @validTo, valid_from = @validFrom, renewal_count = renewal_count + 1
                                    WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@validFrom", newValidFrom.ToString("O"));
                cmd.Parameters.AddWithValue("@validTo", newValidTo.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(conn, tableId.Value, ct);
            }

            // Review E-10: a longer validity is a policy change and belongs in the hash chain.
            await RecordAuditEventInternalAsync(conn, new AuditLogEntry
            {
                TenantId = extendTenant,
                EventType = "CONSENT_EXPIRY_EXTENDED",
                ActorSid = new Sid("SYSTEM"),
                TargetTable = tableId?.ToString() ?? "UNKNOWN",
                Decision = "EXTENDED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, NewValidTo = newValidTo })
            }, ct);
        }
    }

    private async Task<bool> HasConfiguredDataOwnersAsync(SqliteConnection conn, TableIdentifier table, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM TABLE_OWNERS tow
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
            LIMIT 1;";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }
}
