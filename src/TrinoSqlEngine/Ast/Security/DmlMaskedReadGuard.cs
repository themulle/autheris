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
    private const string MaskedRead = "A masked column of the DML target is read by the statement.";
    private const string WholeRow = "A whole-row reference to a DML target with masked columns is not permitted.";

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
                // CR-ADG-36: an injected predicate or mask (inside a secured subquery) names the masked column only to hide it, it is not a read
                if (node is SecurityPredicateExpression or MaskExpression) return false;
                violation = Classify(node, maskedColumns, targetNames);
                return violation is null;
            });

            if (violation is not null) return violation;
        }

        return null;
    }

    private static string? Classify(object node, ISet<string> maskedColumns, ICollection<string> targetNames) => node switch
    {
        ColumnReference column when IsTargetColumn(column.Name, maskedColumns, targetNames) => MaskedRead,
        ColumnReference { Name.Parts.Count: 1 } whole when targetNames.Contains(whole.Name.SimpleName, StringComparer.OrdinalIgnoreCase) => WholeRow,
        WildcardSelectItem { Qualifier: { } qualifier } when targetNames.Contains(qualifier.SimpleName, StringComparer.OrdinalIgnoreCase) => WholeRow,
        UsingJoinCondition using_ when using_.Columns.Any(c => maskedColumns.Contains(c.Value)) => MaskedRead,
        _ => null
    };

    // An unqualified name may be the target's column (or an inner table's: rejecting it is the safe direction); a qualified name
    // is the target's column only when the qualifier names the target (alias or table), never a source alias.
    private static bool IsTargetColumn(SqlQualifiedName name, ISet<string> masked, ICollection<string> targetNames) =>
        masked.Contains(name.SimpleName) &&
        (name.Parts.Count == 1 || targetNames.Contains(name.Parts[^2].Value, StringComparer.OrdinalIgnoreCase));
}
