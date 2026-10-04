namespace Autheris.Domain.Interfaces;

public record TableAccessDecision(
    TableIdentifier Table,
    bool IsAllowed,
    IReadOnlyDictionary<string, ColumnAccessLevel> ColumnAccess,
    string? CombinedRowFilterSql,
    IReadOnlyList<string> DeniedReasons,
    bool HasUnconstrainedColumnAllow = false,
    IReadOnlyDictionary<string, object?>? RowFilterParameters = null
)
{
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
