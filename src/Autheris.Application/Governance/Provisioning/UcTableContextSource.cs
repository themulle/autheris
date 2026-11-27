namespace Autheris.Application.Governance.Provisioning;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>What the reconciler knows about one table for one tenant: metadata, effective protection and tenant-scoped classifications.</summary>
public sealed record UcTableFacts(TableMetadata? Table, EffectiveProtection? Protection, bool HasColumnClassifications);

/// <summary>
/// The governance facts the reconciler needs beyond consents. Every call either answers or throws; a throw ends the data source's
/// cycle without any change, so a missing fact never turns into a grant or a revoke.
/// </summary>
public interface IUcTableContextSource
{
    Task<UcTableFacts> GetTableAsync(TenantId tenant, TableIdentifier table, CancellationToken ct);

    /// <summary>The active pattern consents of the tenant (their denies can cover a provisioned table).</summary>
    Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenant, DateTimeOffset now, CancellationToken ct);

    /// <summary>Whether a virtual filter profile of the tenant may narrow rows.</summary>
    Task<bool> HasMandatoryRowFiltersAsync(TenantId tenant, CancellationToken ct);
}

/// <summary><see cref="IUcTableContextSource"/> over the catalog, protection, consent, classification and virtual filter stores.</summary>
public sealed class UcTableContextSource(
    ITableMetadataRepository tables,
    IProtectionSnapshotProvider protection,
    IConsentRepository consents,
    IClassificationRepository classifications,
    IMandatoryRowFilterResolver mandatoryFilters) : IUcTableContextSource
{
    public async Task<UcTableFacts> GetTableAsync(TenantId tenant, TableIdentifier table, CancellationToken ct)
    {
        var metadata = await tables.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (metadata is null)
        {
            return new UcTableFacts(null, null, false);
        }

        var snapshot = await protection.GetAsync(tenant, table, ct).ConfigureAwait(false);
        var columnIds = metadata.Columns.Where(c => c.Id != Guid.Empty).Select(c => c.Id.ToString()).ToList();
        var classified = false;
        if (columnIds.Count > 0)
        {
            var assigned = await classifications.GetColumnClassificationsBatchAsync(columnIds, tenant.Value, ct).ConfigureAwait(false);
            var overrides = await classifications.GetColumnMaskOverridesBatchAsync(columnIds, tenant.Value, ct).ConfigureAwait(false);
            classified = assigned.Values.Any(rows => rows.Count > 0) || overrides.Values.Any(rows => rows.Count > 0);
        }

        return new UcTableFacts(metadata, snapshot, classified);
    }

    public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenant, DateTimeOffset now, CancellationToken ct) =>
        consents.GetActivePatternConsentsAsync(tenant, now, ct);

    public async Task<bool> HasMandatoryRowFiltersAsync(TenantId tenant, CancellationToken ct) =>
        await mandatoryFilters.HasActiveProfilesAsync(tenant, ct).ConfigureAwait(false);
}
