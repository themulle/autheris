namespace Autheris.Application.Sql.Policy;

using System;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Whether a caller sees a table completely: the decision allows it and no catalog column is hidden from the caller. A masked column
/// is visible (its value is hidden, its existence is not); a denied column is not.
/// </summary>
internal static class ColumnVisibility
{
    public static bool IsFullyVisible(TableAccessDecision decision, TableMetadata meta)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(meta);

        if (!decision.IsAllowed)
        {
            return false;
        }

        foreach (var column in meta.Columns)
        {
            if (decision.GetEffectiveColumnAccess(column.ColumnName, meta) == ColumnAccessLevel.Deny)
            {
                return false;
            }
        }

        return true;
    }
}
