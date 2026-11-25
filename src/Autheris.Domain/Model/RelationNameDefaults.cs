namespace Autheris.Domain.Model;

/// <summary>
/// Issue #1099: the default name of a relation that was declared without a name (a dbt <c>relationships</c> test or
/// foreign-key constraint): the parent model name without a leading <c>stg_</c>. The GraphQL schema recognises it and names
/// the parent-side field after the child table instead, because every unnamed relation of a parent shares that name.
/// </summary>
public static class RelationNameDefaults
{
    private const string StagingPrefix = "stg_";

    /// <summary>The default relation name for a relation to <paramref name="parentName"/>.</summary>
    public static string ForParent(string parentName)
    {
        ArgumentNullException.ThrowIfNull(parentName);
        return parentName.Length > StagingPrefix.Length && parentName.StartsWith(StagingPrefix, StringComparison.Ordinal)
            ? parentName[StagingPrefix.Length..]
            : parentName;
    }

    /// <summary>True when <paramref name="relationName"/> is the default name for the parent table <paramref name="parentTableName"/> (case-insensitive).</summary>
    public static bool IsDefaultFor(string? relationName, string? parentTableName)
    {
        if (string.IsNullOrEmpty(relationName) || string.IsNullOrEmpty(parentTableName))
        {
            return false;
        }

        return string.Equals(relationName, parentTableName, StringComparison.OrdinalIgnoreCase)
               || (parentTableName.Length == StagingPrefix.Length + relationName.Length
                   && parentTableName.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)
                   && parentTableName.EndsWith(relationName, StringComparison.OrdinalIgnoreCase));
    }
}
