namespace Autheris.Domain.Interfaces;

using Autheris.Domain.Exceptions;

public record TableAccessDecision(
    TableIdentifier Table,
    bool IsAllowed,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    string? CombinedRowFilterSql,
    IReadOnlyList<string> DeniedReasons,
    bool HasUnconstrainedColumnAllow = false,
    IReadOnlyDictionary<string, object?>? RowFilterParameters = null,
    string? MandatoryRowPredicateSql = null,
    IReadOnlyList<string>? AppliedVirtualFilters = null
)
{
    /// <summary>Error code of a Virtual Filter that is applied but not (provably) enforced (F-GOV-14).</summary>
    public const string VirtualFilterEnforcementIncompleteCode = "VF_ENFORCEMENT_INCOMPLETE";

    /// <summary>F-GOV-14 (M1): the (filter, binding) pairs behind <see cref="MandatoryRowPredicateSql"/>.</summary>
    public IReadOnlyList<AppliedVirtualFilterBinding>? AppliedVirtualFilterBindings { get; init; }

    /// <summary>F-GOV-14 (M1): the row filter before the Virtual Filter predicate was added (consent filters).</summary>
    public string? RowFilterSqlBeforeVirtualFilters { get; init; }

    /// <summary>F-GOV-14 (M1): row filters added after the Virtual Filter predicate (ABAC), in order.</summary>
    public IReadOnlyList<string>? RowFiltersAfterVirtualFilters { get; init; }

    /// <summary>
    /// Virtual filters (design 3.3): adds a restrictive predicate with AND. It narrows what the consents allow and can
    /// never be lifted by another consent (consent filters are combined with OR, this one is not). The applied pairs
    /// are kept so the gateway can enforce them itself (<see cref="WithoutEnforcedVirtualFilters"/>).
    /// </summary>
    public TableAccessDecision WithMandatoryPredicate(
        string predicateSql,
        IReadOnlyList<string> appliedFilters,
        IReadOnlyList<AppliedVirtualFilterBinding>? appliedBindings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predicateSql);
        ArgumentNullException.ThrowIfNull(appliedFilters);
        if (MandatoryRowPredicateSql != null)
        {
            throw new InvalidOperationException("The decision already carries a Virtual Filter predicate.");
        }

        return this with
        {
            CombinedRowFilterSql = Compose(CombinedRowFilterSql, predicateSql),
            MandatoryRowPredicateSql = predicateSql,
            AppliedVirtualFilters = appliedFilters,
            AppliedVirtualFilterBindings = appliedBindings,
            RowFilterSqlBeforeVirtualFilters = CombinedRowFilterSql,
            RowFiltersAfterVirtualFilters = null
        };
    }

    /// <summary>
    /// Adds a restrictive row filter with AND (<c>({combined}) AND ({added})</c>). Every code path that narrows the row
    /// filter of a decision after the Virtual Filter predicate must use this method, so the filter survives
    /// <see cref="WithoutEnforcedVirtualFilters"/>.
    /// </summary>
    public TableAccessDecision WithAdditionalRowFilter(string rowFilterSql)
    {
        if (string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return this;
        }

        var combined = Compose(CombinedRowFilterSql, rowFilterSql);
        return MandatoryRowPredicateSql == null
            ? this with { CombinedRowFilterSql = combined }
            : this with { CombinedRowFilterSql = combined, RowFiltersAfterVirtualFilters = [.. RowFiltersAfterVirtualFilters ?? [], rowFilterSql] };
    }

    /// <summary>
    /// F-GOV-14 (M1): the residual decision once the gateway enforced the Virtual Filters itself. Only the Virtual
    /// Filter predicate is removed; every other row filter stays. Denies with
    /// <see cref="VirtualFilterEnforcementIncompleteCode"/> when <paramref name="enforced"/> is not exactly the set of
    /// applied pairs (by value identity, including the snapshot generation) or when the row filter text was changed
    /// without <see cref="WithAdditionalRowFilter"/>.
    /// </summary>
    public TableAccessDecision WithoutEnforcedVirtualFilters(IReadOnlyCollection<AppliedVirtualFilterBinding> enforced)
    {
        ArgumentNullException.ThrowIfNull(enforced);
        var applied = AppliedVirtualFilterBindings ?? [];
        if (MandatoryRowPredicateSql == null && applied.Count == 0 && enforced.Count == 0)
        {
            return this;
        }

        if (MandatoryRowPredicateSql == null || applied.Count == 0)
        {
            throw Incomplete("the decision does not identify the applied Virtual Filters");
        }

        var appliedKeys = applied.Select(a => a.Key).ToHashSet();
        var enforcedKeys = enforced.Select(e => e.Key).ToHashSet();
        if (!appliedKeys.SetEquals(enforcedKeys))
        {
            throw Incomplete("the enforcement does not cover exactly the applied Virtual Filters");
        }

        var afters = RowFiltersAfterVirtualFilters ?? [];
        var expected = afters.Aggregate(Compose(RowFilterSqlBeforeVirtualFilters, MandatoryRowPredicateSql), Compose);
        if (!string.Equals(expected, CombinedRowFilterSql, StringComparison.Ordinal))
        {
            throw Incomplete("the row filter was changed outside the decision helpers");
        }

        var residual = afters.Aggregate(RowFilterSqlBeforeVirtualFilters, Compose);
        return this with
        {
            CombinedRowFilterSql = string.IsNullOrWhiteSpace(residual) ? null : residual,
            MandatoryRowPredicateSql = null,
            AppliedVirtualFilters = [],
            AppliedVirtualFilterBindings = [],
            RowFilterSqlBeforeVirtualFilters = null,
            RowFiltersAfterVirtualFilters = null
        };
    }

    private GatewaySecurityException Incomplete(string why) =>
        new($"Virtual filters on '{Table}' are not enforced completely: {why}.", VirtualFilterEnforcementIncompleteCode);

    private static string? Compose(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : $"({left}) AND ({right})";

    public ColumnAccessLevel GetColumnAccess(string columnName)
    {
        if (ColumnAccess.TryGetValue(columnName, out var lvl))
        {
            return lvl;
        }
        return HasUnconstrainedColumnAllow ? ColumnAccessLevel.Clear : ColumnAccessLevel.Deny;
    }

    public bool HasExplicitClear(string columnName) =>
        ColumnAccess.TryGetValue(columnName, out var lvl) && lvl == ColumnAccessLevel.Clear;

    /// <summary>
    /// SEC H-10/C-03: Single source of truth for the effective access level of a column, used for projection AND filtering.
    /// Deny stays Deny; catalog-sensitive columns (IsSensitive or a ColumnMaskingRule) are masked unless an explicit Clear rule exists.
    /// Filtering on a column is only permitted when this method returns <see cref="ColumnAccessLevel.Clear"/>.
    /// </summary>
    public ColumnAccessLevel GetEffectiveColumnAccess(string columnName, TableMetadata? metadata)
    {
        var access = GetColumnAccess(columnName);
        if (access == ColumnAccessLevel.Deny)
        {
            return ColumnAccessLevel.Deny;
        }

        bool isSensitiveInCatalog = false;
        if (metadata != null)
        {
            var column = metadata.GetColumn(columnName);
            isSensitiveInCatalog = column?.IsSensitive == true ||
                                   metadata.ColumnMaskingRules.ContainsKey(columnName) ||
                                   (column != null && metadata.ColumnMaskingRules.ContainsKey(column.ColumnName));
        }

        if (isSensitiveInCatalog && !HasExplicitClear(columnName))
        {
            return ColumnAccessLevel.Mask;
        }

        return access;
    }

    public static TableAccessDecision Denied(TableIdentifier table, params string[] reasons) =>
        new(table, false, new Dictionary<string, ColumnAccessLevel>(), null, reasons, false);

    public static TableAccessDecision Allowed(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        string? rowFilterSql = null,
        bool hasUnconstrainedColumnAllow = false,
        IReadOnlyDictionary<string, object?>? rowFilterParameters = null) =>
        new(table, true, columnAccess, rowFilterSql, Array.Empty<string>(), hasUnconstrainedColumnAllow, rowFilterParameters);
}
