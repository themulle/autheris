namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository
{
    private const string SelectConsentPagePrefix =
        "SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type, c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to, c.is_revoked, c.tenant_id, c.renewal_count, t.source_name, t.schema_name, t.table_name FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id WHERE ";

    private const string SelectConsentPageSuffix =
        " ORDER BY c.id OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY";

    public async Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        TenantScope.RequireTenant(query.Tenant, nameof(GovernanceTable.Consents));

        var size = query.EffectivePageSize;
        var where = new StringBuilder("c.tenant_id = @tenantId");
        if (query.Table is not null)
        {
            where.Append(" AND LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)");
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
            cmd.CommandText = SelectConsentPagePrefix + where.ToString() + SelectConsentPageSuffix;
            cmd.AddBound("@tenantId", query.Tenant.Value);
            cmd.AddBound("@take", size + 1);
            if (query.Table is { } table)
            {
                cmd.AddBound("@domain", table.Domain);
                cmd.AddBound("@schema", table.Schema);
                cmd.AddBound("@table", table.TableName);
            }

            if (query.GranteeType is { } type)
            {
                cmd.AddBound("@gtype", type.ToString());
            }

            if (granteeNorm != null)
            {
                cmd.AddBound("@gnorm", granteeNorm);
            }

            if (query.AfterId is { } after)
            {
                cmd.AddBound("@after", after.ToString());
            }

            await using (var reader = await ExecuteReaderAsync(cmd, "ConsentPage.GetPage", ct).ConfigureAwait(false))
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
                        TenantId = StoredTenant.Parse(reader.IsDBNull(11) ? null : reader.GetString(11)),
                        RenewalCount = reader.GetInt32(12),
                        TableIdentifier = new TableIdentifier(reader.GetString(13), reader.GetString(14), reader.GetString(15))
                    });
                }
            }
        }

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);

        Guid? nextCursor = null;
        if (more && consents.Count > 0)
        {
            nextCursor = consents[^1].Id;
        }

        return new ConsentPage(consents, nextCursor);
    }
}
