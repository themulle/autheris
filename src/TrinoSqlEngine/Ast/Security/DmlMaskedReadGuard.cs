namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Finds reads of masked columns of a DML target in the user part of a statement (SET right-hand sides, WHERE, MERGE ON and WHEN
/// conditions, inserted values). A masked column must not be readable there: it would be a copy-out or comparison oracle
/// (SEC-ADG-08 b, INV-10). Used by the injector and, independently, by the coverage verifier.
/// </summary>
internal static class DmlMaskedReadGuard
{
    /// <returns>A violation description (no names, no values), or null when the parts are clean.</returns>
    public static string? FindViolation(ISet<string> maskedColumns, ICollection<string> targetNames, params object?[] userParts)
    {
        if (maskedColumns.Count == 0) return null;
        string? violation = null;
        foreach (var part in userParts)
        {
            AstReflection.Walk(part, node =>
            {
                if (violation is not null) return false;
                switch (node)
                {
                    case SecurityPredicateExpression:
                        return false;
                    case MaskExpression:
                        // CR-ADG-36: an injected mask (inside a secured subquery) names the masked column only to hide it, it is not a read
                        return false;
                    case ColumnReference column when IsTargetColumn(column.Name, maskedColumns, targetNames):
                        violation = "A masked column of the DML target is read by the statement.";
                        return false;
                    case ColumnReference { Name.Parts.Count: 1 } whole when targetNames.Contains(whole.Name.SimpleName, StringComparer.OrdinalIgnoreCase):
                        violation = "A whole-row reference to a DML target with masked columns is not permitted.";
                        return false;
                    case WildcardSelectItem { Qualifier: { } qualifier } when targetNames.Contains(qualifier.SimpleName, StringComparer.OrdinalIgnoreCase):
                        violation = "A whole-row reference to a DML target with masked columns is not permitted.";
                        return false;
                    case UsingJoinCondition using_ when using_.Columns.Any(c => maskedColumns.Contains(c.Value)):
                        violation = "A masked column of the DML target is read by the statement.";
                        return false;
                }

                return true;
            });

            if (violation is not null) return violation;
        }

        return null;
    }

    // An unqualified name may be the target's column (or an inner table's: rejecting it is the safe direction); a qualified name
    // is the target's column only when the qualifier names the target (alias or table), never a source alias.
    private static bool IsTargetColumn(SqlQualifiedName name, ISet<string> masked, ICollection<string> targetNames) =>
        masked.Contains(name.SimpleName) &&
        (name.Parts.Count == 1 || targetNames.Contains(name.Parts[^2].Value, StringComparer.OrdinalIgnoreCase));
}
