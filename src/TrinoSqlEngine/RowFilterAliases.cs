namespace TrinoSqlEngine;

/// <summary>
/// Reserved aliases shared by row-filter generation and the query builders.
/// </summary>
public static class RowFilterAliases
{
    /// <summary>
    /// Alias under which every governed SELECT exposes the filtered table. Correlated row filters
    /// (EXISTS ... WHERE dep.pk = autheris_target.fk) reference the target table only through this alias.
    /// </summary>
    public const string Target = "autheris_target";

    public static bool ReferencesTarget(string? filterSql) =>
        !string.IsNullOrEmpty(filterSql) && filterSql.Contains(Target, System.StringComparison.OrdinalIgnoreCase);
}
