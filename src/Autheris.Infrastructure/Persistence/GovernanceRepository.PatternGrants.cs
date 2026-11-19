using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

public partial class GovernanceRepository
{
    private const string SelectActiveDirectOwnersForTableSql =
        "SELECT o.ad_sid FROM TABLES t " +
        "JOIN TABLE_OWNERS tow ON t.id = tow.table_id " +
        "JOIN DATA_OWNERS o ON tow.data_owner_id = o.id " +
        "WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table) " +
        "AND o.is_active = 1";

    private const string SelectActiveDelegatesForTableSql =
        "SELECT d.delegate_sid FROM DATA_OWNER_DELEGATIONS d " +
        "JOIN DATA_OWNERS o ON d.data_owner_id = o.id " +
        "JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id " +
        "JOIN TABLES t ON tow.table_id = t.id " +
        "WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table) " +
        "AND o.is_active = 1 AND d.valid_from <= @now AND d.valid_to >= @now";

    public async Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(
        TenantId tenantId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        var scoped = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var consents = new List<Consent>();
        await ConsentPatternSqlCore.ReadActivePatternConsentsForDomainAsync(
            conn, domain: null, scoped, atTime, subjectSet: null, seen, consents, ct).ConfigureAwait(false);
        consents.RemoveAll(c => !c.IsActive(atTime));
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<IReadOnlyList<Consent>> GetActiveInternalPatternConsentsAsync(
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var consents = new List<Consent>();
        await ConsentPatternSqlCore.ReadActivePatternConsentsForDomainAsync(
            conn, domain: null, tenantId: null, atTime, subjectSet: null, seen, consents, ct).ConfigureAwait(false);
        consents.RemoveAll(c =>
            !c.IsActive(atTime) ||
            c.Effect != ConsentEffect.Allow ||
            c.WorkflowMode != ConsentWorkflowMode.Internal);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<IReadOnlySet<string>> GetAuthorizedApproverSidsForTableAsync(
        TableIdentifier table,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var ownerCmd = conn.CreateCommand())
        {
            ownerCmd.CommandText = SelectActiveDirectOwnersForTableSql;
            ownerCmd.AddBound("@domain", table.Domain);
            ownerCmd.AddBound("@schema", table.Schema);
            ownerCmd.AddBound("@table", table.TableName);

            await using var reader = await ExecuteReaderAsync(ownerCmd, "PatternGrants.ReadDirectOwners", ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var sid = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        set.Add(sid);
                    }
                }
            }
        }

        await using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = SelectActiveDelegatesForTableSql;
            delCmd.AddBound("@domain", table.Domain);
            delCmd.AddBound("@schema", table.Schema);
            delCmd.AddBound("@table", table.TableName);
            delCmd.AddBound("@now", atTime.ToString("O"));

            await using var reader = await ExecuteReaderAsync(delCmd, "PatternGrants.ReadDelegates", ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var sid = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(sid))
                    {
                        set.Add(sid);
                    }
                }
            }
        }

        return set;
    }

    public async Task<bool> IsAnyApproverAuthorizedForTableAsync(
        TableIdentifier table,
        IReadOnlyCollection<string> ownerSids,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        if (ownerSids.Count == 0)
        {
            return false;
        }

        var authorized = await GetAuthorizedApproverSidsForTableAsync(table, atTime, ct).ConfigureAwait(false);
        foreach (var candidate in ownerSids)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && authorized.Contains(candidate.Trim()))
            {
                return true;
            }
        }

        return false;
    }
}
