namespace Autheris.Application.Sql.Federation.Conjuncts;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using TrinoSqlEngine.Ast.Nodes;

public sealed record SingleTableConjunct(
    TableIdentifier Table,
    Expression Predicate,
    IReadOnlyList<string> Columns,
    ExtractedConjunct SourceConjunct);

public sealed record MultiTableConjunct(
    IReadOnlyList<TableIdentifier> Tables,
    Expression Predicate,
    ExtractedConjunct SourceConjunct);

public sealed record ConjunctClassificationResult(
    IReadOnlyList<SingleTableConjunct> SingleTableConjuncts,
    IReadOnlyList<MultiTableConjunct> MultiTableConjuncts);

/// <summary>
/// Attributes conjunct columns to source tables and isolates single-table pushdown candidates.
/// Preserves single-table disjunctions (OR) while classifying cross-table disjunctions as MultiTable.
/// </summary>
public static class SingleTableConjunctClassifier
{
    public static ConjunctClassificationResult Classify(
        IReadOnlyList<ExtractedConjunct> conjuncts,
        IReadOnlyList<ResolvedSourceTable> tables)
    {
        ArgumentNullException.ThrowIfNull(conjuncts);
        ArgumentNullException.ThrowIfNull(tables);

        var singleTable = new List<SingleTableConjunct>();
        var multiTable = new List<MultiTableConjunct>();

        foreach (var conjunct in conjuncts)
        {
            var colRefs = new List<ColumnReference>();
            CollectColumnReferences(conjunct.Expression, colRefs);

            if (colRefs.Count == 0)
            {
                multiTable.Add(new MultiTableConjunct(Array.Empty<TableIdentifier>(), conjunct.Expression, conjunct));
                continue;
            }

            var referencedTables = new HashSet<ResolvedSourceTable>();
            var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool unresolvable = false;

            foreach (var colRef in colRefs)
            {
                var (table, colName) = ResolveColumn(colRef, tables);
                if (table == null)
                {
                    unresolvable = true;
                    break;
                }

                referencedTables.Add(table);
                columnNames.Add(colName);
            }

            if (unresolvable || referencedTables.Count != 1)
            {
                multiTable.Add(new MultiTableConjunct(
                    referencedTables.Select(t => t.ResolvedIdentifier).Distinct().ToList(),
                    conjunct.Expression,
                    conjunct));
                continue;
            }

            var targetTable = referencedTables.First();

            if (conjunct.AllowedTableSources != null)
            {
                bool allowed = conjunct.AllowedTableSources.Any(s => MatchesTableSource(s, targetTable));
                if (!allowed)
                {
                    multiTable.Add(new MultiTableConjunct(
                        [targetTable.ResolvedIdentifier],
                        conjunct.Expression,
                        conjunct));
                    continue;
                }
            }

            singleTable.Add(new SingleTableConjunct(
                targetTable.ResolvedIdentifier,
                conjunct.Expression,
                columnNames.ToList(),
                conjunct));
        }

        return new ConjunctClassificationResult(singleTable, multiTable);
    }

    public static void CollectColumnReferences(SqlNode node, List<ColumnReference> collector)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case ColumnReference col:
                    collector.Add(col);
                    break;
                case ParenthesizedExpression paren:
                    stack.Push(paren.Expression);
                    break;
                case BinaryExpression binary:
                    stack.Push(binary.Right);
                    stack.Push(binary.Left);
                    break;
                case UnaryExpression unary:
                    stack.Push(unary.Operand);
                    break;
                case LikeExpression like:
                    if (like.Escape != null) stack.Push(like.Escape);
                    stack.Push(like.Pattern);
                    stack.Push(like.Operand);
                    break;
                case InListExpression inList:
                    for (int i = inList.Items.Count - 1; i >= 0; i--) stack.Push(inList.Items[i]);
                    stack.Push(inList.Operand);
                    break;
                case BetweenExpression between:
                    stack.Push(between.Upper);
                    stack.Push(between.Lower);
                    stack.Push(between.Operand);
                    break;
                case CaseExpression caseExpr:
                    if (caseExpr.ElseResult != null) stack.Push(caseExpr.ElseResult);
                    for (int i = caseExpr.WhenClauses.Count - 1; i >= 0; i--)
                    {
                        stack.Push(caseExpr.WhenClauses[i].Result);
                        stack.Push(caseExpr.WhenClauses[i].Condition);
                    }
                    if (caseExpr.Operand != null) stack.Push(caseExpr.Operand);
                    break;
                case FunctionCallExpression fn:
                    if (fn.Filter != null) stack.Push(fn.Filter);
                    for (int i = fn.Arguments.Count - 1; i >= 0; i--) stack.Push(fn.Arguments[i]);
                    break;
                case CastExpression cast:
                    stack.Push(cast.Operand);
                    break;
                case IsDistinctFromExpression distinct:
                    stack.Push(distinct.Right);
                    stack.Push(distinct.Left);
                    break;
            }
        }
    }

    internal static (ResolvedSourceTable? Table, string ColumnName) ResolveColumn(
        ColumnReference colRef,
        IReadOnlyList<ResolvedSourceTable> tables)
    {
        if (colRef.Name.Parts.Count == 1)
        {
            var colName = colRef.Name.Parts[0].Value;
            var matching = tables.Where(t => t.Metadata.HasColumn(colName)).ToList();
            if (matching.Count == 1)
            {
                return (matching[0], colName);
            }

            return (null, colName);
        }
        else
        {
            var colName = colRef.Name.Parts[^1].Value;
            var qualifier = string.Join(".", colRef.Name.Parts.Take(colRef.Name.Parts.Count - 1).Select(p => p.Value));

            var matching = tables.Where(t =>
                SqlDataSourceResolver.ReferencesColumn(qualifier, colName, colName, t.Target) ||
                string.Equals(qualifier, t.Target.Alias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, t.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, t.Target.FullName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, t.ResolvedIdentifier.ToQualifiedName(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qualifier, t.ResolvedIdentifier.TableName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matching.Count == 1)
            {
                return (matching[0], colName);
            }

            return (null, colName);
        }
    }

    private static bool MatchesTableSource(TableSource source, ResolvedSourceTable table)
    {
        if (source is NamedTableSource named)
        {
            if (named.Alias != null && !string.IsNullOrWhiteSpace(named.Alias.Value))
            {
                if (string.Equals(named.Alias.Value, table.Target.Alias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(named.Alias.Value, table.Target.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var rawName = string.Join(".", named.Name.Parts.Select(p => p.Value));
            var lastName = named.Name.Parts[^1].Value;

            return string.Equals(rawName, table.Target.FullName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawName, table.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(lastName, table.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawName, table.ResolvedIdentifier.ToString(), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(rawName, table.ResolvedIdentifier.ToQualifiedName(), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(lastName, table.ResolvedIdentifier.TableName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
