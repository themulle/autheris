using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
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

        var consents = new List<Consent>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                              AND c.is_revoked = 0" + tenantFilter;
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        if (tenantId != null)
        {
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
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

        var consents = new List<Consent>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

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

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(
        Guid consentRequestId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        var consents = new List<Consent>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.consent_request_id = @reqId";
        cmd.Parameters.AddWithValue("@reqId", consentRequestId.ToString());

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var validFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture);
                var validTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture);

                if (atTime < validFrom || atTime >= validTo) continue;

                var gTypeStr = reader.GetString(4);
                var granteeType = Enum.Parse<GranteeType>(gTypeStr, true);
                var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                var domain = reader.GetString(11);
                var tableIdentifier = new TableIdentifier(domain, reader.GetString(12), reader.GetString(13));

                var consent = new Consent
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    TableId = Guid.Parse(reader.GetString(1)),
                    TableIdentifier = tableIdentifier,
                    ConsentRequestId = consentRequestId,
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

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    private async Task HydrateConsentsBatchAsync(NpgsqlConnection conn, List<Consent> consents, CancellationToken ct)
    {
        if (consents.Count == 0) return;

        var consentIds = consents.Select(c => c.Id.ToString()).ToList();
        var rulesMap = consents.ToDictionary(c => c.Id.ToString(), _ => new List<ConsentColumnRule>(), StringComparer.OrdinalIgnoreCase);

        // Fetch Column Rules
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT consent_id, table_column_id, column_name, access_level FROM CONSENT_COLUMN_RULES WHERE consent_id = ANY(@ids)";
            cmd.Parameters.AddWithValue("@ids", consentIds.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var cId = reader.GetString(0);
                if (rulesMap.TryGetValue(cId, out var ruleList))
                {
                    ruleList.Add(new ConsentColumnRule
                    {
                        Id = Guid.NewGuid(),
                        ConsentId = Guid.Parse(cId),
                        TableColumnId = Guid.Parse(reader.GetString(1)),
                        ColumnName = reader.GetString(2),
                        AccessLevel = (ColumnAccessLevel)reader.GetInt32(3)
                    });
                }
            }
        }

        foreach (var c in consents)
        {
            c.ColumnRules = rulesMap[c.Id.ToString()];
        }

        var filtersMap = consents.ToDictionary(c => c.Id.ToString(), _ => new List<ConsentRowFilter>(), StringComparer.OrdinalIgnoreCase);

        // Fetch Row Filters
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                                       filter_type, dependent_table, dependent_table_alias, foreign_key_column, primary_key_column,
                                       subquery_predicate_json, target_temporal_column, dependent_valid_from_column, dependent_valid_to_column,
                                       target_table_alias, additional_hops_json
                                FROM CONSENT_ROW_FILTERS
                                WHERE consent_id = ANY(@ids)";
            cmd.Parameters.AddWithValue("@ids", consentIds.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var cId = reader.GetString(0);
                if (filtersMap.TryGetValue(cId, out var filterList))
                {
                    var ftInt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                    TableIdentifier? depTable = null;
                    if (!reader.IsDBNull(10) && TableIdentifier.TryParse(reader.GetString(10), out var dt))
                    {
                        depTable = dt;
                    }

                    IReadOnlyList<SubqueryJoinHop>? hops = null;
                    var hopsJson = reader.IsDBNull(19) ? null : reader.GetString(19);
                    if (!string.IsNullOrWhiteSpace(hopsJson))
                    {
                        try
                        {
                            hops = JsonSerializer.Deserialize<List<SubqueryJoinHop>>(hopsJson);
                        }
                        catch (JsonException ex)
                        {
                            // Review PG-11: a row filter that cannot be read must not silently lose its join hops (fail-closed, as in SQLite).
                            throw new InvalidOperationException("Malformed additional hops JSON detected in consent row filter.", ex);
                        }
                    }

                    filterList.Add(new ConsentRowFilter
                    {
                        Id = Guid.NewGuid(),
                        ConsentId = Guid.Parse(cId),
                        FilterGroup = reader.GetInt32(1),
                        TableColumnId = Guid.Parse(reader.GetString(2)),
                        ColumnName = reader.GetString(3),
                        Operator = reader.GetString(4),
                        ValueType = reader.GetString(5),
                        ValueJson = reader.GetString(6),
                        ValueSource = reader.GetString(7),
                        UserAttribute = reader.IsDBNull(8) ? null : reader.GetString(8),
                        FilterType = (RowFilterType)ftInt,
                        DependentTable = depTable,
                        DependentTableAlias = reader.IsDBNull(11) ? null : reader.GetString(11),
                        ForeignKeyColumn = reader.IsDBNull(12) ? null : reader.GetString(12),
                        PrimaryKeyColumn = reader.IsDBNull(13) ? null : reader.GetString(13),
                        SubqueryFilterPredicateJson = reader.IsDBNull(14) ? null : reader.GetString(14),
                        TargetTemporalColumn = reader.IsDBNull(15) ? null : reader.GetString(15),
                        DependentValidFromColumn = reader.IsDBNull(16) ? null : reader.GetString(16),
                        DependentValidToColumn = reader.IsDBNull(17) ? null : reader.GetString(17),
                        TargetTableAlias = reader.IsDBNull(18) ? null : reader.GetString(18),
                        AdditionalHops = hops
                    });
                }
            }
        }

        foreach (var c in consents)
        {
            c.RowFilters = filtersMap[c.Id.ToString()];
        }
    }

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        consent.Validate(); // Review PG-11: same input validation as SQLite
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var consentId = consent.Id == Guid.Empty ? Guid.NewGuid() : consent.Id;
            var tableId = consent.TableId;

            if (tableId == Guid.Empty)
            {
                await using var findTableCmd = conn.CreateCommand();
                findTableCmd.Transaction = tx;
                findTableCmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) LIMIT 1";
                findTableCmd.Parameters.AddWithValue("@domain", consent.TableIdentifier.Domain);
                findTableCmd.Parameters.AddWithValue("@schema", consent.TableIdentifier.Schema);
                findTableCmd.Parameters.AddWithValue("@table", consent.TableIdentifier.TableName);
                var idObj = await findTableCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (idObj == null)
                {
                    throw new InvalidOperationException($"Cannot create consent: Target table '{consent.TableIdentifier}' does not exist in catalog.");
                }
                tableId = Guid.Parse(idObj.ToString()!);
            }

            var tenantIdStr = string.IsNullOrWhiteSpace(consent.TenantId.Value) ? TenantId.LegacySingleTenant.Value : consent.TenantId.Value;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                    VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @roleId, @roleName, @validFrom, @validTo, 0, @tenantId)";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@tid", tableId.ToString());
                cmd.Parameters.AddWithValue("@reqId", (object?)consent.ConsentRequestId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@effect", consent.Effect.ToString());
                cmd.Parameters.AddWithValue("@granteeType", consent.GranteeType.ToString());
                cmd.Parameters.AddWithValue("@granteeSid", (object?)consent.GranteeSid?.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@roleId", (object?)consent.RoleId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@roleName", (object?)consent.RoleName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@validFrom", consent.ValidFrom.ToString("O"));
                cmd.Parameters.AddWithValue("@validTo", consent.ValidTo.ToString("O"));
                cmd.Parameters.AddWithValue("@tenantId", tenantIdStr);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var rule in consent.ColumnRules)
            {
                var ruleId = Guid.NewGuid();
                await using var ruleCmd = conn.CreateCommand();
                ruleCmd.Transaction = tx;
                ruleCmd.CommandText = @"INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                                        VALUES (@id, @consentId, @colId, @colName, @level)";
                ruleCmd.Parameters.AddWithValue("@id", ruleId.ToString());
                ruleCmd.Parameters.AddWithValue("@consentId", consentId.ToString());
                ruleCmd.Parameters.AddWithValue("@colId", rule.TableColumnId.ToString());
                ruleCmd.Parameters.AddWithValue("@colName", rule.ColumnName);
                ruleCmd.Parameters.AddWithValue("@level", (int)rule.AccessLevel);
                await ruleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (var filter in consent.RowFilters)
            {
                var filterId = Guid.NewGuid();
                var hopsJson = filter.AdditionalHops != null && filter.AdditionalHops.Count > 0
                    ? JsonSerializer.Serialize(filter.AdditionalHops)
                    : null;

                await using var filterCmd = conn.CreateCommand();
                filterCmd.Transaction = tx;
                filterCmd.CommandText = @"INSERT INTO CONSENT_ROW_FILTERS (
                                            id, consent_id, filter_group, table_column_id, column_name, operator, value_type, value_json, value_source, user_attribute,
                                            filter_type, dependent_table, dependent_table_alias, foreign_key_column, primary_key_column,
                                            subquery_predicate_json, target_temporal_column, dependent_valid_from_column, dependent_valid_to_column,
                                            target_table_alias, additional_hops_json
                                        ) VALUES (
                                            @id, @consentId, @fg, @colId, @colName, @op, @vType, @vJson, @vSource, @uAttr,
                                            @ft, @dt, @dta, @fk, @pk, @sqp, @ttc, @dvfc, @dvtc, @tta, @hops
                                        )";
                filterCmd.Parameters.AddWithValue("@id", filterId.ToString());
                filterCmd.Parameters.AddWithValue("@consentId", consentId.ToString());
                filterCmd.Parameters.AddWithValue("@fg", filter.FilterGroup);
                filterCmd.Parameters.AddWithValue("@colId", filter.TableColumnId.ToString());
                filterCmd.Parameters.AddWithValue("@colName", filter.ColumnName);
                filterCmd.Parameters.AddWithValue("@op", filter.Operator);
                filterCmd.Parameters.AddWithValue("@vType", filter.ValueType);
                filterCmd.Parameters.AddWithValue("@vJson", filter.ValueJson);
                filterCmd.Parameters.AddWithValue("@vSource", filter.ValueSource);
                filterCmd.Parameters.AddWithValue("@uAttr", (object?)filter.UserAttribute ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@ft", (int)filter.FilterType);
                filterCmd.Parameters.AddWithValue("@dt", filter.DependentTable.HasValue ? (object)filter.DependentTable.Value.ToString() : DBNull.Value);
                filterCmd.Parameters.AddWithValue("@dta", (object?)filter.DependentTableAlias ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@fk", (object?)filter.ForeignKeyColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@pk", (object?)filter.PrimaryKeyColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@sqp", (object?)filter.SubqueryFilterPredicateJson ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@ttc", (object?)filter.TargetTemporalColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@dvfc", (object?)filter.DependentValidFromColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@dvtc", (object?)filter.DependentValidToColumn ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@tta", (object?)filter.TargetTableAlias ?? DBNull.Value);
                filterCmd.Parameters.AddWithValue("@hops", (object?)hopsJson ?? DBNull.Value);
                await filterCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            _metadataCache.TryRemove(consent.TableIdentifier.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);

            // Review E-10: parity with SQLite - a directly created consent is recorded with the creator as actor.
            await TryRecordAuditAfterCommitAsync(new AuditLogEntry
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
            }, ct).ConfigureAwait(false);

            return new Consent
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
                RowFilters = consent.RowFilters
            };
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        Consent? consent = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                       c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.id = @id";
            cmd.Parameters.AddWithValue("@id", consentId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var domain = reader.GetString(11);
                var tableIdentifier = new TableIdentifier(domain, reader.GetString(12), reader.GetString(13));
                var granteeType = Enum.Parse<GranteeType>(reader.GetString(4), true);
                var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

                consent = new Consent
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
                    ValidFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                    ValidTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                    IsRevoked = reader.GetInt32(10) == 1,
                    TenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14))
                };
            }
        }

        if (consent != null)
        {
            await HydrateConsentsBatchAsync(conn, new List<Consent> { consent }, ct).ConfigureAwait(false);
        }

        return consent;
    }

    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? tableId = null;
        TenantId tenantId = TenantId.LegacySingleTenant;
        bool isGranteeSelf = false;
        bool alreadyRevoked = false;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var getTableCmd = conn.CreateCommand())
            {
                getTableCmd.Transaction = tx;
                getTableCmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                            FROM CONSENTS c
                                            JOIN TABLES t ON c.table_id = t.id
                                            WHERE c.id = @id
                                            FOR UPDATE OF c";
                getTableCmd.Parameters.AddWithValue("@id", consentId.ToString());
                await using var reader = await getTableCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    if (!reader.IsDBNull(3) && TenantId.TryParse(reader.GetString(3), out var tid))
                    {
                        tenantId = tid;
                    }

                    var gType = reader.GetString(4);
                    var gSid = reader.IsDBNull(5) ? null : reader.GetString(5);
                    isGranteeSelf = string.Equals(gType, "User", StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(gSid, revokedBySid.Value, StringComparison.OrdinalIgnoreCase);
                    alreadyRevoked = reader.GetInt32(6) == 1;
                }
            }

            // Review PG-9: a missing consent is an error (as in SQLite), not a silent no-op.
            if (!tableId.HasValue)
            {
                throw new KeyNotFoundException($"Consent mit ID '{consentId}' existiert nicht.");
            }

            // Review PG-9: authorization in the repository (as in SQLite): the grantee itself or an approver of the table.
            if (!isGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(tableId.Value, revokedBySid, null, ct).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException($"Benutzer '{revokedBySid}' ist weder Data Owner oder delegierter Genehmiger für '{tableId.Value}', noch der Begünstigte selbst.");
            }

            // Review PG-9 / Low-13: an existing revocation (who, when, why) is never overwritten.
            if (alreadyRevoked)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE CONSENTS
                                    SET is_revoked = 1, revoked_by_sid = @sid, revoked_at = @now, revoke_reason = @reason
                                    WHERE id = @id AND is_revoked = 0";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@sid", revokedBySid.Value);
                cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@reason", reason);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(conn, tx, tableId.Value, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (tableId.HasValue)
        {
            _metadataCache.TryRemove(tableId.Value.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "CONSENT_REVOKED",
            ActorSid = revokedBySid,
            TargetTable = tableId.HasValue ? tableId.Value.ToString() : "unknown",
            Decision = "REVOKE",
            TraceId = Guid.NewGuid().ToString(),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, Reason = reason }),
            TenantId = tenantId
        };
        await RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? tableId = null;
        TenantId tenantId = TenantId.LegacySingleTenant;

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var getCmd = conn.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id
                                       FROM CONSENTS c
                                       JOIN TABLES t ON c.table_id = t.id
                                       WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0
                                       FOR UPDATE";
                getCmd.Parameters.AddWithValue("@id", consentId.ToString());
                getCmd.Parameters.AddWithValue("@reqId", consentRequestId.ToString());

                await using var reader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }

                tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!reader.IsDBNull(3) && TenantId.TryParse(reader.GetString(3), out var tid))
                {
                    tenantId = tid;
                }
            }

            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"UPDATE CONSENTS
                                          SET is_revoked = 1, revoked_by_sid = @sid, revoked_at = @now, revoke_reason = @reason
                                          WHERE id = @id";
                updateCmd.Parameters.AddWithValue("@id", consentId.ToString());
                updateCmd.Parameters.AddWithValue("@sid", revokedBySid.Value);
                updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                updateCmd.Parameters.AddWithValue("@reason", reason);
                await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(conn, tx, tableId.Value, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (tableId.HasValue)
        {
            _metadataCache.TryRemove(tableId.Value.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "CONSENT_REVOKED",
            ActorSid = revokedBySid,
            TargetTable = tableId.HasValue ? tableId.Value.ToString() : "unknown",
            Decision = "REVOKE",
            TraceId = Guid.NewGuid().ToString(),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, ConsentRequestId = consentRequestId, Reason = reason }),
            TenantId = tenantId
        };
        await RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, CancellationToken ct = default)
    {
        var consents = new List<Consent>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.valid_to <= @threshold";
        cmd.Parameters.AddWithValue("@threshold", threshold.ToString("O"));

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var domain = reader.GetString(11);
                var tableIdentifier = new TableIdentifier(domain, reader.GetString(12), reader.GetString(13));
                var granteeType = Enum.Parse<GranteeType>(reader.GetString(4), true);
                var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);

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
                    ValidFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                    ValidTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                    IsRevoked = reader.GetInt32(10) == 1,
                    TenantId = reader.IsDBNull(14) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(14))
                };
                consents.Add(consent);
            }
        }

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        TableIdentifier? tableId = null;
        TenantId extendTenant = TenantId.LegacySingleTenant;
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var getCmd = conn.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id
                                       FROM CONSENTS c
                                       JOIN TABLES t ON c.table_id = t.id
                                       WHERE c.id = @id
                                       FOR UPDATE OF c";
                getCmd.Parameters.AddWithValue("@id", consentId.ToString());
                await using var reader = await getCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                    if (!reader.IsDBNull(3))
                    {
                        extendTenant = new TenantId(reader.GetString(3));
                    }
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE CONSENTS SET valid_to = @validTo WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@validTo", newValidTo.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(conn, tx, tableId.Value, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (tableId.HasValue)
        {
            _metadataCache.TryRemove(tableId.Value.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }

        // Review E-10: a longer validity is a policy change and belongs in the hash chain.
        await TryRecordAuditAfterCommitAsync(new AuditLogEntry
        {
            TenantId = extendTenant,
            EventType = "CONSENT_EXPIRY_EXTENDED",
            ActorSid = new Sid("SYSTEM"),
            TargetTable = tableId?.ToString() ?? "unknown",
            Decision = "EXTENDED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, NewValidTo = newValidTo })
        }, ct).ConfigureAwait(false);
    }

    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        string? tableId = null;
        if (request.TableId != Guid.Empty)
        {
            tableId = request.TableId.ToString();
        }
        else
        {
            await using (var findCmd = conn.CreateCommand())
            {
                findCmd.CommandText = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) LIMIT 1";
                findCmd.Parameters.AddWithValue("@domain", request.TableIdentifier.Domain);
                findCmd.Parameters.AddWithValue("@schema", request.TableIdentifier.Schema);
                findCmd.Parameters.AddWithValue("@table", request.TableIdentifier.TableName);
                tableId = (await findCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            }
        }

        if (tableId == null)
        {
            throw new InvalidOperationException($"Cannot create consent request: Target table '{request.TableIdentifier}' does not exist in catalog.");
        }

        var reqId = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id;
        var tableGuid = Guid.Parse(tableId);

        var reqIdentifiersJson = request.RequesterIdentifiers != null && request.RequesterIdentifiers.Count > 0
            ? JsonSerializer.Serialize(request.RequesterIdentifiers)
            : null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO CONSENT_REQUESTS (
                                    id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref,
                                    business_justification, status, requested_at, requested_valid_to,
                                    itsm_ticket_id, tenant_id, requester_identifiers_json
                                ) VALUES (
                                    @id, @tid, @sid, @gType, @gRef,
                                    @just, @status, @reqAt, @reqTo,
                                    @ticket, @tenantId, @reqIds
                                )";
            cmd.Parameters.AddWithValue("@id", reqId.ToString());
            cmd.Parameters.AddWithValue("@tid", tableId);
            cmd.Parameters.AddWithValue("@sid", request.RequesterSid.Value);
            cmd.Parameters.AddWithValue("@gType", request.RequestedGranteeType.ToString());
            cmd.Parameters.AddWithValue("@gRef", request.RequestedGranteeRef);
            cmd.Parameters.AddWithValue("@just", request.BusinessJustification);
            cmd.Parameters.AddWithValue("@status", request.Status);
            cmd.Parameters.AddWithValue("@reqAt", request.RequestedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@reqTo", request.RequestedValidTo.ToString("O"));
            cmd.Parameters.AddWithValue("@ticket", (object?)request.ItsmTicketId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tenantId", request.TenantId.Value);
            cmd.Parameters.AddWithValue("@reqIds", (object?)reqIdentifiersJson ?? DBNull.Value);
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
            ApprovalSteps = request.ApprovalSteps ?? new List<ApprovalStep>()
        };
    }


    public async Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        ConsentRequest? req = null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       t.source_name, t.schema_name, t.table_name, r.itsm_ticket_id, r.tenant_id,
                                       r.requester_identifiers_json
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                WHERE r.id = @id";
            cmd.Parameters.AddWithValue("@id", requestId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var reqIdsJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                var reqIds = !string.IsNullOrWhiteSpace(reqIdsJson)
                    ? JsonSerializer.Deserialize<List<string>>(reqIdsJson)
                    : null;

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
                    ItsmTicketId = reader.IsDBNull(12) ? null : reader.GetString(12),
                    TenantId = reader.IsDBNull(13) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(13)),
                    RequesterIdentifiers = reqIds ?? new List<string>()
                };
            }
        }

        if (req != null)
        {
            await using var stepCmd = conn.CreateCommand();
            stepCmd.CommandText = @"SELECT step_number, approver_sid, decision, rejection_reason, decided_at
                                    FROM APPROVAL_STEPS
                                    WHERE consent_request_id = @id
                                    ORDER BY step_number ASC";
            stepCmd.Parameters.AddWithValue("@id", requestId.ToString());
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

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        var list = new List<ConsentRequest>();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                   r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                   t.source_name, t.schema_name, t.table_name
                            FROM CONSENT_REQUESTS r
                            JOIN TABLES t ON r.table_id = t.id
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

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval: false, itsmApproverAccount: null, ct);

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount: null, ct);

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, isExternalItsmApproval);

        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
        {
            throw new InvalidOperationException("Funktionstrennung verletzt: Antragsteller darf eigenen Antrag nicht genehmigen.");
        }

        // Review R4-4 (rest): compare by the stable data owner id as well, not only by spelling.
        var approverOwnerIds = await ResolveDataOwnerIdsInternalAsync(ConsentApprovalPolicy.IdentifierCandidates(approverSid.Value, itsmApproverAccount), ct).ConfigureAwait(false);
        if (ConsentApprovalPolicy.ShareOwnerId(approverOwnerIds, await ResolveRequesterOwnerIdsInternalAsync(req, ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("Funktionstrennung verletzt: Antragsteller darf eigenen Antrag nicht genehmigen.");
        }

        // Authorization is checked before the row lock is taken: the checks use their own pooled connections.
        if (!isExternalItsmApproval)
        {
            bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, null, ct).ConfigureAwait(false);
            if (!isAuthorized)
            {
                throw new UnauthorizedAccessException($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für Tabelle '{req.TableIdentifier}'.");
            }
        }
        else
        {
            // SEC N-1 / Review R3-1 + R2-5 (same rule as SQLite): tables with configured owners need a named ITSM approver
            // who is an owner, delegate or admin (matched by account or e-mail, passed typed, never parsed from the SID).
            bool hasConfiguredOwners = await HasConfiguredDataOwnersAsync(req.TableIdentifier, ct).ConfigureAwait(false);
            if (hasConfiguredOwners)
            {
                string? account = string.IsNullOrWhiteSpace(itsmApproverAccount) ? null : itsmApproverAccount.Trim();
                if (account == null)
                {
                    throw new UnauthorizedAccessException($"ITSM-Freigabe ohne benannten Genehmiger ist für Tabelle '{req.TableIdentifier}' mit konfigurierten Data Ownern nicht zulässig.");
                }

                bool isAuthorized = await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, account, ct).ConfigureAwait(false);
                if (!isAuthorized)
                {
                    throw new UnauthorizedAccessException($"ITSM-Genehmiger '{account}' ist weder Data Owner noch delegierter Genehmiger für Tabelle '{req.TableIdentifier}'.");
                }
            }
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id FOR UPDATE";
                checkCmd.Parameters.AddWithValue("@id", requestId.ToString());
                var statusObj = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
                currentStatus = statusObj.ToString()!;
            }

            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval);

            bool requiresFourEyes = false;
            string? tableSensitivity = null;
            await using (var feCmd = conn.CreateCommand())
            {
                feCmd.Transaction = tx;
                feCmd.CommandText = @"SELECT t.requires_four_eyes, t.sensitivity
                                      FROM TABLES t
                                      JOIN CONSENT_REQUESTS r ON t.id = r.table_id
                                      WHERE r.id = @id";
                feCmd.Parameters.AddWithValue("@id", requestId.ToString());
                await using var reader = await feCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
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

            var existingApprovers = new List<string>();
            await using (var approversCmd = conn.CreateCommand())
            {
                approversCmd.Transaction = tx;
                approversCmd.CommandText = "SELECT approver_sid FROM APPROVAL_STEPS WHERE consent_request_id = @id AND decision = 'APPROVED' ORDER BY step_number";
                approversCmd.Parameters.AddWithValue("@id", requestId.ToString());
                await using var reader = await approversCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    existingApprovers.Add(reader.GetString(0));
                }
            }

            // Review R4-4: compare the approver identity (account part), not the raw actor string.
            bool sameApprover = existingApprovers.Any(s => ConsentApprovalPolicy.IsSameApprover(s, approverSid, itsmApproverAccount));
            for (int i = 0; !sameApprover && i < existingApprovers.Count; i++)
            {
                var storedOwnerIds = await ResolveDataOwnerIdsInternalAsync(ConsentApprovalPolicy.IdentifierCandidates(existingApprovers[i]), ct).ConfigureAwait(false);
                sameApprover = ConsentApprovalPolicy.ShareOwnerId(storedOwnerIds, approverOwnerIds);
            }

            if (sameApprover)
            {
                if (isExternalItsmApproval)
                {
                    // Redelivered ITSM webhook for a step that is already recorded: idempotent, nothing changes.
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return req;
                }

                throw new InvalidOperationException("Vier-Augen-Prinzip verletzt: Genehmiger hat diesen Antrag bereits genehmigt.");
            }

            int stepNumber;
            await using (var maxCmd = conn.CreateCommand())
            {
                maxCmd.Transaction = tx;
                maxCmd.CommandText = "SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @id";
                maxCmd.Parameters.AddWithValue("@id", requestId.ToString());
                stepNumber = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            await using (var stepInsert = conn.CreateCommand())
            {
                stepInsert.Transaction = tx;
                stepInsert.CommandText = @"INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, decided_at)
                                           VALUES (@id, @reqId, @step, @sid, 'APPROVED', @now)";
                stepInsert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                stepInsert.Parameters.AddWithValue("@reqId", requestId.ToString());
                stepInsert.Parameters.AddWithValue("@step", stepNumber);
                stepInsert.Parameters.AddWithValue("@sid", approverSid.Value);
                stepInsert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await stepInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Review PG-4 (same contract as SQLite): the final approval yields APPROVED; the caller creates the consent
            // (GraphQL: with column snapshot) or calls ActivateConsentAsync (ITSM). Review E-9: ITSM keeps its second step.
            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(requiresFourEyes, stepNumber, isExternalItsmApproval, tableSensitivity);

            await using (var updateReq = conn.CreateCommand())
            {
                updateReq.Transaction = tx;
                updateReq.CommandText = "UPDATE CONSENT_REQUESTS SET status = @status WHERE id = @id";
                updateReq.Parameters.AddWithValue("@status", newStatus);
                updateReq.Parameters.AddWithValue("@id", requestId.ToString());
                await updateReq.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            req.Status = newStatus;
            await TryRecordAuditAfterCommitAsync(
                ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, itsmApproverAccount, null), ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The business transaction is already committed; a failing audit enqueue must not turn it into an error.</summary>
    private async Task TryRecordAuditAfterCommitAsync(AuditLogEntry entry, CancellationToken ct)
    {
        try
        {
            await RecordAuditEventAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Audit event {EventType} could not be recorded after the transaction was committed.", entry.EventType);
        }
    }

    private async Task<bool> HasConfiguredDataOwnersAsync(TableIdentifier table, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // Review PG-3: any owner row counts (as in SQLite), also deactivated owners, so that deactivating all owners
        // cannot switch the approver check off.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) =>
        RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm: false, ct);

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!ConsentApprovalPolicy.IsRejectableStatus(req.Status))
        {
            throw new InvalidOperationException($"Request {requestId} is in status '{req.Status}' and cannot be rejected.");
        }

        // Review PG-5: authorization at repository level (as in SQLite): ITSM actors, or owner/delegate/admin of the table.
        bool isAuthorized = isExternalItsm ||
                            await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, null, ct).ConfigureAwait(false);
        if (!isAuthorized)
        {
            throw new UnauthorizedAccessException($"Benutzer '{approverSid}' ist weder Data Owner noch delegierter Genehmiger für Tabelle '{req.TableIdentifier}'.");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus;
            await using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id FOR UPDATE";
                checkCmd.Parameters.AddWithValue("@id", requestId.ToString());
                var statusObj = await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (statusObj == null) throw new InvalidOperationException($"Request {requestId} not found.");
                currentStatus = statusObj.ToString()!;
            }

            if (!ConsentApprovalPolicy.IsRejectableStatus(currentStatus))
            {
                throw new InvalidOperationException($"Request {requestId} is in status '{currentStatus}' and cannot be rejected.");
            }

            await using (var stepInsert = conn.CreateCommand())
            {
                stepInsert.Transaction = tx;
                stepInsert.CommandText = @"INSERT INTO APPROVAL_STEPS (id, consent_request_id, step_number, approver_sid, decision, rejection_reason, decided_at)
                                           VALUES (@id, @reqId, (SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @reqId), @sid, 'REJECTED', @reason, @now)";
                stepInsert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                stepInsert.Parameters.AddWithValue("@reqId", requestId.ToString());
                stepInsert.Parameters.AddWithValue("@sid", approverSid.Value);
                stepInsert.Parameters.AddWithValue("@reason", reason);
                stepInsert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await stepInsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var updateReq = conn.CreateCommand())
            {
                updateReq.Transaction = tx;
                updateReq.CommandText = "UPDATE CONSENT_REQUESTS SET status = 'REJECTED' WHERE id = @id";
                updateReq.Parameters.AddWithValue("@id", requestId.ToString());
                await updateReq.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            req.Status = "REJECTED";
            await TryRecordAuditAfterCommitAsync(
                ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, reason), ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM CONSENT_REQUESTS WHERE itsm_ticket_id = @ticket ORDER BY requested_at, id LIMIT 1";
        cmd.Parameters.AddWithValue("@ticket", ticketId);
        var idObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (idObj == null) return null;
        return await GetConsentRequestAsync(Guid.Parse(idObj.ToString()!), ct).ConfigureAwait(false);
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    /// <summary>Insecure getting-started auto-approve path only: activates a request that is still pending.</summary>
    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private async Task ActivateConsentCoreAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"ConsentRequest {requestId} not found.");

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var updateReq = conn.CreateCommand())
            {
                updateReq.Transaction = tx;
                // SEC H-06 (as SQLite): only a pending or approved request may be activated; REJECTED/other states never.
                // Review G5: regular activation only from 'APPROVED' (i.e. after the approval steps); pending states only for dev auto-approve.
                updateReq.CommandText = allowPendingAutoApprove
                    ? @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                        WHERE id = @id AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL', 'APPROVED')"
                    : @"UPDATE CONSENT_REQUESTS SET status = 'APPROVED'
                        WHERE id = @id AND status = 'APPROVED'";
                updateReq.Parameters.AddWithValue("@id", requestId.ToString());
                if (await updateReq.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return;
                }
            }

            await using (var existsCmd = conn.CreateCommand())
            {
                existsCmd.Transaction = tx;
                // SEC N-2: never activate the same request twice (also backed by a unique index).
                existsCmd.CommandText = "SELECT COUNT(1) FROM CONSENTS WHERE consent_request_id = @reqId";
                existsCmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                if (Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return;
                }
            }

            var consentId = Guid.NewGuid();
            var granteeSid = req.RequestedGranteeType == GranteeType.User ? new Sid(req.RequestedGranteeRef) : (Sid?)null;
            var roleName = req.RequestedGranteeType == GranteeType.Role ? req.RequestedGranteeRef : null;

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, role_name, valid_from, valid_to, is_revoked, tenant_id)
                                    VALUES (@id, @tid, @reqId, 'ALLOW', @granteeType, @granteeSid, @roleName, @validFrom, @validTo, 0, @tenantId)";
                cmd.Parameters.AddWithValue("@id", consentId.ToString());
                cmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                cmd.Parameters.AddWithValue("@reqId", requestId.ToString());
                cmd.Parameters.AddWithValue("@granteeType", req.RequestedGranteeType.ToString());
                cmd.Parameters.AddWithValue("@granteeSid", (object?)granteeSid?.Value ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@roleName", (object?)roleName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@validFrom", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@validTo", req.RequestedValidTo.ToString("O"));
                cmd.Parameters.AddWithValue("@tenantId", req.TenantId.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // POL-3: Freeze full column snapshot from table metadata on ITSM activation
            var columnRules = new List<(Guid Id, Guid ColId, string ColName, int AccessLevel)>();
            await using (var colCmd = conn.CreateCommand())
            {
                colCmd.Transaction = tx;
                colCmd.CommandText = @"SELECT c.id, c.column_name, c.is_sensitive,
                                              MAX(CASE WHEN m.id IS NOT NULL THEN 1 ELSE 0 END) AS has_mask
                                       FROM TABLE_COLUMNS c
                                       LEFT JOIN COLUMN_MASKING_RULES m ON c.id = m.table_column_id
                                       WHERE c.table_id = @tid
                                       GROUP BY c.id, c.column_name, c.is_sensitive";
                colCmd.Parameters.AddWithValue("@tid", req.TableId.ToString());
                await using var reader = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var colId = Guid.Parse(reader.GetString(0));
                    var colName = reader.GetString(1);
                    var isSensitive = Convert.ToInt32(reader.GetValue(2)) == 1;
                    var hasMask = Convert.ToInt32(reader.GetValue(3)) == 1;
                    int accessLevel = (isSensitive || hasMask)
                        ? (int)Autheris.Domain.Interfaces.ColumnAccessLevel.Mask
                        : (int)Autheris.Domain.Interfaces.ColumnAccessLevel.Clear;
                    columnRules.Add((Guid.NewGuid(), colId, colName, accessLevel));
                }
            }

            foreach (var (ruleId, colId, colName, accessLevel) in columnRules)
            {
                await using var ruleCmd = conn.CreateCommand();
                ruleCmd.Transaction = tx;
                ruleCmd.CommandText = @"INSERT INTO CONSENT_COLUMN_RULES (id, consent_id, table_column_id, column_name, access_level)
                                        VALUES (@id, @consentId, @colId, @colName, @level)";
                ruleCmd.Parameters.AddWithValue("@id", ruleId.ToString());
                ruleCmd.Parameters.AddWithValue("@consentId", consentId.ToString());
                ruleCmd.Parameters.AddWithValue("@colId", colId.ToString());
                ruleCmd.Parameters.AddWithValue("@colName", colName);
                ruleCmd.Parameters.AddWithValue("@level", accessLevel);
                await ruleCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await IncrementTableEpochInternalAsync(conn, tx, req.TableIdentifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        _metadataCache.TryRemove(req.TableIdentifier.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(req.TableIdentifier, ct).ConfigureAwait(false);

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "CONSENT_GRANTED",
            ActorSid = approvedBy ?? req.RequesterSid,
            TargetTable = req.TableIdentifier.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString(),
            DetailsJson = JsonSerializer.Serialize(new { RequestId = requestId, Grantee = req.RequestedGranteeRef }),
            TenantId = req.TenantId
        };
        await RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);
    }

    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var stepCmd = conn.CreateCommand())
            {
                stepCmd.Transaction = tx;
                stepCmd.CommandText = "DELETE FROM APPROVAL_STEPS WHERE consent_request_id = @id";
                stepCmd.Parameters.AddWithValue("@id", requestId.ToString());
                await stepCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM CONSENT_REQUESTS WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", requestId.ToString());
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE CONSENT_REQUESTS SET itsm_ticket_id = @ticket WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", requestId.ToString());
        cmd.Parameters.AddWithValue("@ticket", ticketId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
