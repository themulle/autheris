namespace Autheris.Application.Mcp.Interfaces;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

/// <summary>
/// Caller view used by <see cref="IGoldenQueryService"/> to serve stored golden queries. The store entry set is tenant and catalog-version
/// scoped and caller independent; every delegate below is evaluated per caller, after the entries are read and before any result cap.
/// A delegate that throws drops the affected entry (fail closed). A missing <see cref="IsDatasetVisible"/> denies every entry; use
/// <see cref="AllVisible"/> for callers that see the whole catalog.
/// </summary>
/// <param name="Tenant">The caller's tenant; a missing, default or reserved tenant serves no stored entries.</param>
/// <param name="IsDatasetVisible">True when the dataset (domain, table) is discoverable for the caller.</param>
/// <param name="HiddenIdentifiers">Column names and GraphQL field names of the dataset the caller must not see (Deny, discovery-hidden or hidden catalog text).</param>
/// <param name="ReferencedDatasets">Datasets, other than the owner, that a query reaches through relation fields; <c>null</c> drops the entry (it cannot be proven safe).</param>
public sealed record GoldenQueryReadContext(
    TenantId? Tenant,
    Func<string, string, bool>? IsDatasetVisible = null,
    Func<string, string, IReadOnlySet<string>?>? HiddenIdentifiers = null,
    Func<Autheris.Domain.Model.GoldenQuery, IEnumerable<(string Domain, string Table)>?>? ReferencedDatasets = null)
{
    /// <summary>A context of a caller that may see every dataset (administrators, bypassed authentication).</summary>
    public static GoldenQueryReadContext AllVisible(TenantId? tenant) => new(tenant, static (_, _) => true);
}
