namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// CR-ADG-33: a MERGE source qualifier must never equal the target qualifier or the target table name (case-insensitive), so the
/// injected target predicates can never bind to the source, whatever the backend's name resolution does.
/// </summary>
internal static class MergeAliasGuard
{
    /// <summary>The effective qualifiers a top-level MERGE source introduces (alias, or the table's own name).</summary>
    public static void CollectSourceQualifiers(TableSource source, ICollection<string> into)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (source)
        {
            case NamedTableSource named:
                into.Add(named.Alias?.Value ?? named.Name.SimpleName);
                break;
            case SubqueryTableSource sub:
                into.Add(sub.Alias.Value);
                break;
            case LateralTableSource lateral:
                into.Add(lateral.Alias.Value);
                break;
            case JoinedTableSource join:
                CollectSourceQualifiers(join.Left, into);
                CollectSourceQualifiers(join.Right, into);
                break;
        }
    }

    /// <summary>Returns true when no source qualifier collides with a target name.</summary>
    public static bool IsDisjoint(TableSource source, IEnumerable<string> targetNames)
    {
        var qualifiers = new List<string>();
        CollectSourceQualifiers(source, qualifiers);
        var targets = new HashSet<string>(targetNames, StringComparer.OrdinalIgnoreCase);
        foreach (var q in qualifiers)
        {
            if (targets.Contains(q)) return false;
        }

        return true;
    }
}
