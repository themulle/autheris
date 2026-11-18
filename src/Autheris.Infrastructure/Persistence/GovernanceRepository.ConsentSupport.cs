namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
public partial class GovernanceRepository
{
    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ConsentSqlCore.DeleteConsentRequestRowsAsync(conn, tx, requestId, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task HydrateConsentsBatchAsync(DbConnection conn, List<Consent> consents, CancellationToken ct)
    {
        await ConsentSqlCore.HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        await ConsentPatternSqlCore.HydrateApprovingOwnersBatchAsync(conn, consents, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(IEnumerable<string> identifiers, CancellationToken ct)
    {
        const string resolveDataOwnerIdsSql = @"SELECT id FROM DATA_OWNERS WHERE LOWER(ad_sid) = LOWER(@p) OR LOWER(ad_account) = LOWER(@p) OR LOWER(email) = LOWER(@p)";
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in identifiers.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = resolveDataOwnerIdsSql;
            cmd.AddBound("@p", identifier);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }
        return ids;
    }

    private async Task<IReadOnlyCollection<string>> ResolveRequesterOwnerIdsInternalAsync(ConsentRequest req, CancellationToken ct)
    {
        var identifiers = ConsentApprovalPolicy.IdentifierCandidates(req.RequesterSid.Value).ToList();
        if (req.RequesterIdentifiers != null)
        {
            identifiers.AddRange(req.RequesterIdentifiers);
        }

        return await ResolveDataOwnerIdsInternalAsync(identifiers, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await IsAuthorizedApproverForTableInternalAsync(conn, null, table, approverSid, ct).ConfigureAwait(false);
    }
}
