namespace Autheris.Application.Policy.Classification;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Issue #1043: the persisted column classifications and column mask overrides of a set of tables, read for one tenant in a few
/// set-based statements before a batch of access decisions (<c>IAccessDecisionPoint.DecideManyAsync</c>), so each decision does not
/// read them again per table. It holds exactly the rows the per-table reads of <see cref="ClassificationAccessResolver"/> return for
/// the covered columns; a column it does not cover, another tenant, or a decision more than <see cref="Autheris.Application.Policy.ConsentBatchPrefetch.MaxAge"/>
/// after <see cref="ReadAt"/> is read as before. It lives for one batch of decisions only.
/// </summary>
public sealed class ColumnPolicyRowPrefetch
{
    private static readonly IReadOnlyList<ColumnClassification> NoClassifications = [];
    private static readonly IReadOnlyList<ColumnMaskOverride> NoOverrides = [];

    private readonly HashSet<string> _covered;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> _classifications;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> _overrides;
    private readonly ClassificationPolicyCatalog? _catalog;

    public ColumnPolicyRowPrefetch(
        string tenantId,
        DateTimeOffset readAt,
        IReadOnlyCollection<string> coveredColumnIds,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> classifications,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> overrides,
        ClassificationPolicyCatalog? catalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(coveredColumnIds);
        TenantId = tenantId;
        ReadAt = readAt;
        _covered = new HashSet<string>(coveredColumnIds, StringComparer.OrdinalIgnoreCase);
        _classifications = classifications ?? throw new ArgumentNullException(nameof(classifications));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _catalog = catalog;
    }

    public string TenantId { get; }

    public DateTimeOffset ReadAt { get; }

    /// <summary>Returns the tenant classification catalog captured for this prefetch while it covers <paramref name="tenantId"/> and is fresh at <paramref name="now"/>.</summary>
    public ClassificationPolicyCatalog? CatalogFor(string tenantId, DateTimeOffset now) =>
        _catalog is not null &&
        string.Equals(TenantId, tenantId, StringComparison.Ordinal) &&
        now >= ReadAt && now - ReadAt <= Autheris.Application.Policy.ConsentBatchPrefetch.MaxAge
            ? _catalog
            : null;

    /// <summary>True when the prefetch was read for <paramref name="tenantId"/>, is fresh at <paramref name="now"/> and covers every one of <paramref name="columnIds"/>.</summary>
    public bool Covers(string tenantId, IReadOnlyCollection<string> columnIds, DateTimeOffset now)
    {
        if (!string.Equals(TenantId, tenantId, StringComparison.Ordinal) ||
            now < ReadAt || now - ReadAt > Autheris.Application.Policy.ConsentBatchPrefetch.MaxAge)
        {
            return false;
        }

        foreach (var id in columnIds)
        {
            if (!_covered.Contains(id))
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<ColumnClassification> ClassificationsOf(string columnId) =>
        _classifications.TryGetValue(columnId, out var rows) ? rows : NoClassifications;

    public IReadOnlyList<ColumnMaskOverride> OverridesOf(string columnId) =>
        _overrides.TryGetValue(columnId, out var rows) ? rows : NoOverrides;
}
