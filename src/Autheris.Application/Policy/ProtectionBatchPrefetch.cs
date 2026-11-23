namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The protection snapshots of a batch of access decisions (<c>IAccessDecisionPoint.DecideManyAsync</c>), resolved once at <see cref="ReadAt"/>
/// from the metadata those decisions use (<see cref="IProtectionSnapshotProvider.ResolveManyAsync"/>) instead of a snapshot load per table.
/// Like <see cref="ConsentBatchPrefetch"/> it answers only for its own tenant, only for the tables it resolved and only within
/// <see cref="ConsentBatchPrefetch.MaxAge"/> of <see cref="ReadAt"/>; any other decision resolves its snapshot per table as before. It lives
/// for one batch only and is never cached.
/// </summary>
public sealed class ProtectionBatchPrefetch
{
    private readonly IReadOnlyDictionary<TableIdentifier, EffectiveProtection> _byTable;

    private ProtectionBatchPrefetch(TenantId tenant, DateTimeOffset readAt, IReadOnlyDictionary<TableIdentifier, EffectiveProtection> byTable)
    {
        Tenant = tenant;
        ReadAt = readAt;
        _byTable = byTable;
    }

    public TenantId Tenant { get; }

    public DateTimeOffset ReadAt { get; }

    /// <summary>Resolves the snapshots of <paramref name="tables"/> for <paramref name="tenant"/> at <paramref name="readAt"/>.</summary>
    public static async Task<ProtectionBatchPrefetch> LoadAsync(
        IProtectionSnapshotProvider provider,
        TenantId tenant,
        IReadOnlyList<TableMetadata> tables,
        DateTimeOffset readAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(tables);
        var byTable = await provider.ResolveManyAsync(tenant, tables, ct).ConfigureAwait(false);
        return new ProtectionBatchPrefetch(tenant, readAt, byTable);
    }

    /// <summary>The snapshot of <paramref name="table"/> when this prefetch resolved it for <paramref name="tenant"/> and is fresh at <paramref name="now"/>.</summary>
    public bool TryGet(TenantId tenant, TableIdentifier table, DateTimeOffset now, out EffectiveProtection protection)
    {
        if (tenant == Tenant &&
            now >= ReadAt &&
            now - ReadAt <= ConsentBatchPrefetch.MaxAge &&
            _byTable.TryGetValue(table, out var found))
        {
            protection = found;
            return true;
        }

        protection = null!;
        return false;
    }
}
