namespace Autheris.Infrastructure.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Revokes a catalog grant and writes its audit entry in one governance transaction, the same way virtual-filter and
/// caller-attribute writes carry their audit: the entry follows an applied revoke and commits with it; a failed audit write
/// rolls the revoke back, and a revoke that did not apply writes no entry.
/// </summary>
public sealed class CatalogGrantRevocationWriter(GovernanceRepository repository, CatalogVisibilityGrantStore store) : ICatalogGrantRevocationWriter
{
    private readonly GovernanceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly CatalogVisibilityGrantStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<bool> RevokeWithAuditAsync(
        TenantId tenantId,
        Guid grantId,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        AuditLogEntry audit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.TenantId != tenantId)
        {
            throw new ArgumentException("The audit entry must belong to the tenant of the revoked grant.", nameof(audit));
        }

        var (revoked, epoch) = await _repository.WriteWithAuditAsync(
            (conn, tx) => _store.RevokeInTransactionAsync(conn, tx, tenantId, grantId, revokedBySid, reason, revokedAt, ct),
            static result => result.Revoked,
            [audit],
            ct).ConfigureAwait(false);

        if (revoked)
        {
            await _store.NotifyRevokedAsync(tenantId, epoch, revokedAt, ct).ConfigureAwait(false);
        }

        return revoked;
    }
}
