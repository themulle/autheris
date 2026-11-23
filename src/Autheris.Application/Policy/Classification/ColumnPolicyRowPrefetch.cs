namespace Autheris.Application.Policy.Classification;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// Issue #1043: the persisted column classifications and column mask overrides of a set of tables, read for one tenant in a few
/// set-based statements before a batch of access decisions (<c>IAccessDecisionPoint.DecideManyAsync</c>), so each decision does not
/// read them again per table. It holds exactly the rows the per-table reads of <see cref="ClassificationAccessResolver"/> return for
/// the covered columns; a column it does not cover, or another tenant, is read as before. It lives for one batch of decisions only.
/// </summary>
public sealed class ColumnPolicyRowPrefetch
{
    private static readonly IReadOnlyList<ColumnClassification> NoClassifications = [];
    private static readonly IReadOnlyList<ColumnMaskOverride> NoOverrides = [];

    private readonly HashSet<string> _covered;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> _classifications;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> _overrides;

    public ColumnPolicyRowPrefetch(
        string tenantId,
        IReadOnlyCollection<string> coveredColumnIds,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> classifications,
        IReadOnlyDictionary<string, IReadOnlyList<ColumnMaskOverride>> overrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(coveredColumnIds);
        TenantId = tenantId;
        _covered = new HashSet<string>(coveredColumnIds, StringComparer.OrdinalIgnoreCase);
        _classifications = classifications ?? throw new ArgumentNullException(nameof(classifications));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
    }

    public string TenantId { get; }

    /// <summary>True when the prefetch was read for <paramref name="tenantId"/> and covers every one of <paramref name="columnIds"/>.</summary>
    public bool Covers(string tenantId, IReadOnlyCollection<string> columnIds)
    {
        if (!string.Equals(TenantId, tenantId, StringComparison.Ordinal))
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
