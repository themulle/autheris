using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;


namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
{
    /// <summary>
    /// WP-SC-10: one keyset page (ordered by consent id) of the consents of ONE tenant. The tenant predicate is mandatory (an empty tenant
    /// is refused, fail closed). Activity is not decided here; the caller applies <see cref="Consent.IsActive"/> at read time.
    /// </summary>
    public async Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Tenant.Value))
        {
            throw new ArgumentException("A consent page query requires a tenant.", nameof(query));
        }

        var size = query.EffectivePageSize;
        var where = new System.Text.StringBuilder("c.tenant_id = @tenantId");
        if (query.Table is not null)
        {
            where.Append(" AND t.source_name_lc = LOWER(@domain) AND t.schema_name_lc = LOWER(@schema) AND t.table_name_lc = LOWER(@table)");
        }

        if (query.GranteeType is not null)
        {
            where.Append(" AND c.grantee_type = @gtype");
        }

        var granteeNorm = string.IsNullOrWhiteSpace(query.Grantee) ? null : ConsentSubjectFilter.Normalize(query.Grantee.Trim());
        if (granteeNorm != null)
        {
            where.Append(" AND (c.grantee_sid_norm = @gnorm OR UPPER(c.role_name) = @gnorm)");
        }

        if (!query.IncludeInactive)
        {
            where.Append(" AND c.is_revoked = 0");
        }

        if (query.AfterId is not null)
        {
            where.Append(" AND c.id > @after");
        }

        var consents = new List<Consent>();
        var more = false;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT TOP (@take) c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type, c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to, c.is_revoked, c.tenant_id, c.renewal_count, t.source_name, t.schema_name, t.table_name FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id WHERE {where} ORDER BY c.id";
            cmd.Parameters.AddWithValue("@tenantId", query.Tenant.Value);
            cmd.Parameters.AddWithValue("@take", size + 1);
            if (query.Table is { } table)
            {
                cmd.Parameters.AddWithValue("@domain", table.Domain);
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.TableName);
            }

            if (query.GranteeType is { } type)
            {
                cmd.Parameters.AddWithValue("@gtype", type.ToString());
            }

            if (granteeNorm != null)
            {
                cmd.Parameters.AddWithValue("@gnorm", granteeNorm);
            }

            if (query.AfterId is { } after)
            {
                cmd.Parameters.AddWithValue("@after", after.ToString());
            }

            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (consents.Count == size)
                    {
                        more = true;
                        break;
                    }

                    consents.Add(new Consent
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        TableId = Guid.Parse(reader.GetString(1)),
                        ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                        GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), true),
                        GranteeSid = reader.IsDBNull(5) ? (Sid?)null : new Sid(reader.GetString(5)),
                        RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                        RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                        ValidFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                        ValidTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                        IsRevoked = reader.GetInt32(10) == 1,
                        TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(11)),
                        RenewalCount = reader.GetInt32(12),
                        TableIdentifier = new TableIdentifier(reader.GetString(13), reader.GetString(14), reader.GetString(15))
                    });
                }
            }
        }

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return new ConsentPage(consents, more ? consents[^1].Id : null);
    }
}
