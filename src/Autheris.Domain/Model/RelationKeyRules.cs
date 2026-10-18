namespace Autheris.Domain.Model;

/// <summary>Reason a relation key pair list is invalid (issue #151).</summary>
public enum RelationKeyViolation
{
    MissingColumns,
    CountMismatch,
    EmptyName,
    DuplicateColumn,
    TooWide
}

/// <summary>Single source of truth for relation join key rules (issue #151, SEC-REL151-03).</summary>
public static class RelationKeyRules
{
    public const int MaxKeyColumns = 16;

    /// <summary>Returns the first violation found, or null when the ordered pairs are valid.</summary>
    public static RelationKeyViolation? Validate(IReadOnlyList<string> parent, IReadOnlyList<string> child)
    {
        if (parent is null || child is null || parent.Count == 0 || child.Count == 0)
        {
            return RelationKeyViolation.MissingColumns;
        }

        if (parent.Count != child.Count)
        {
            return RelationKeyViolation.CountMismatch;
        }

        if (parent.Count > MaxKeyColumns)
        {
            return RelationKeyViolation.TooWide;
        }

        if (HasEmpty(parent) || HasEmpty(child))
        {
            return RelationKeyViolation.EmptyName;
        }

        if (HasDuplicate(parent) || HasDuplicate(child))
        {
            return RelationKeyViolation.DuplicateColumn;
        }

        return null;
    }

    private static bool HasEmpty(IReadOnlyList<string> columns)
    {
        foreach (var c in columns)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDuplicate(IReadOnlyList<string> columns)
    {
        var seen = new HashSet<string>(columns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
        {
            if (!seen.Add(c))
            {
                return true;
            }
        }

        return false;
    }
}
