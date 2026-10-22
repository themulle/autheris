namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class AstSecurityVisitor
{
    private const int MaxRecursiveMaskRounds = 64;

    /// <summary>
    /// SEC-277-10: the masked output columns of a recursive CTE. The CTE name is already in scope (the caller added it), so a
    /// self-reference inherits the current set; the set is the least fixed point over every member (anchor and recursive members). A masked
    /// value that a member places at output position <c>i</c> marks the output column named at position <c>i</c> (the CTE column list, else
    /// the anchor's names), so renaming between members cannot hide a mask. A member whose positions cannot be resolved (wildcard) marks all
    /// output columns; when the output names themselves cannot be resolved the statement is rejected (fail closed).
    /// </summary>
    private HashSet<string> CollectRecursiveCteMaskedColumns(CommonTableExpression cte, string cteKey)
    {
        var members = new List<QueryBody>(4);
        FlattenSetOperationMembers(cte.Query.Body, members);
        string?[]? outputNames = RecursiveOutputNames(cte, members[0]);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int round = 0; round < MaxRecursiveMaskRounds; round++)
        {
            if (result.Count > 0)
            {
                _cteMaskedColumns.Peek()[cteKey] = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
            }

            var next = new HashSet<string>(result, StringComparer.OrdinalIgnoreCase);
            next.UnionWith(CollectMaskedOutputColumns(cte.Query, cte.ColumnAliases));
            for (int m = 0; m < members.Count; m++)
            {
                AddMemberMaskedByPosition(members[m], m == 0, outputNames, next);
            }

            if (next.Count == result.Count)
            {
                return next;
            }

            result = next;
        }

        throw new SecurityException("A masked column in a recursive CTE could not be tracked.");
    }

    private static void FlattenSetOperationMembers(QueryBody body, List<QueryBody> members)
    {
        if (body is SetOperationQuery set)
        {
            FlattenSetOperationMembers(set.Left, members);
            FlattenSetOperationMembers(set.Right, members);
            return;
        }

        members.Add(body);
    }

    private static string?[]? RecursiveOutputNames(CommonTableExpression cte, QueryBody anchor)
    {
        if (cte.ColumnAliases is { Count: > 0 } aliases)
        {
            var named = new string?[aliases.Count];
            for (int i = 0; i < named.Length; i++) named[i] = aliases[i].Value;
            return named;
        }

        if (anchor is not QuerySpecification spec)
        {
            return null;
        }

        var names = new string?[spec.Projections.Count];
        for (int i = 0; i < names.Length; i++)
        {
            switch (spec.Projections[i])
            {
                case ColumnSelectItem item:
                    names[i] = item.Alias?.Value ?? (item.Expression is ColumnReference column ? column.Name.SimpleName : null);
                    break;
                default:
                    return null;   // a wildcard: the number and names of the output columns are not known without the catalog
            }
        }

        return names;
    }

    private void AddMemberMaskedByPosition(QueryBody member, bool isAnchor, string?[]? outputNames, HashSet<string> result)
    {
        var byName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMaskedOutputColumnsFromBody(member, null, byName);
        if (byName.Count == 0)
        {
            return;   // no masked value reaches the output of this member
        }

        if (member is QuerySpecification { From: not null } spec)
        {
            var tables = new List<(string TableName, string? Alias)>();
            CollectTableSources(spec.From, tables);
            var derived = new List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)>();
            CollectDerivedMaskedSources(spec.From, derived);

            bool hasWildcard = false;
            var positions = new List<int>();
            for (int i = 0; i < spec.Projections.Count; i++)
            {
                if (spec.Projections[i] is WildcardSelectItem)
                {
                    hasWildcard = true;
                }
                else if (spec.Projections[i] is ColumnSelectItem item && ExpressionReferencesMaskedColumn(item.Expression, tables, derived))
                {
                    positions.Add(i);
                }
            }

            if (!hasWildcard)
            {
                foreach (int position in positions)
                {
                    if (outputNames is null)
                    {
                        if (isAnchor) break;   // the anchor defines the names itself: the by-name set above is exact
                        throw new SecurityException("A masked column in a recursive CTE could not be tracked.");
                    }

                    if (position >= outputNames.Length)
                    {
                        MarkAllOutputColumns(outputNames, result);
                        break;
                    }

                    result.Add(outputNames[position] ?? throw new SecurityException("A masked column in a recursive CTE could not be tracked."));
                }

                return;
            }
        }

        // A wildcard, TABLE or VALUES member: the positions are unknown, so every output column is marked.
        if (outputNames is null)
        {
            if (isAnchor) return;   // a wildcard anchor keeps the source names: the by-name set above is exact
            throw new SecurityException("A masked column in a recursive CTE could not be tracked.");
        }

        MarkAllOutputColumns(outputNames, result);
    }

    private static void MarkAllOutputColumns(string?[] outputNames, HashSet<string> result)
    {
        foreach (string? name in outputNames)
        {
            result.Add(name ?? throw new SecurityException("A masked column in a recursive CTE could not be tracked."));
        }
    }
}
