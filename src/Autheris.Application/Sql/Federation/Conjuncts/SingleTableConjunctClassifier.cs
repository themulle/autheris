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
/// WP-122-1: Attributes conjunct columns to source tables and isolates single-table pushdown candidates.
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
        switch (node)
        {
            case ColumnReference col:
                collector.Add(col);
                break;
            case ParenthesizedExpression paren:
                CollectColumnReferences(paren.Expression, collector);
                break;
            case BinaryExpression binary:
                CollectColumnReferences(binary.Left, collector);
                CollectColumnReferences(binary.Right, collector);
                break;
            case UnaryExpression unary:
                CollectColumnReferences(unary.Operand, collector);
                break;
            case LikeExpression like:
                CollectColumnReferences(like.Operand, collector);
                CollectColumnReferences(like.Pattern, collector);
                if (like.Escape != null) CollectColumnReferences(like.Escape, collector);
                break;
            case InListExpression inList:
                CollectColumnReferences(inList.Operand, collector);
                foreach (var item in inList.Items) CollectColumnReferences(item, collector);
                break;
            case BetweenExpression between:
                CollectColumnReferences(between.Operand, collector);
                CollectColumnReferences(between.Lower, collector);
                CollectColumnReferences(between.Upper, collector);
                break;
            case CaseExpression caseExpr:
                if (caseExpr.Operand != null) CollectColumnReferences(caseExpr.Operand, collector);
                foreach (var when in caseExpr.WhenClauses)
                {
                    CollectColumnReferences(when.Condition, collector);
                    CollectColumnReferences(when.Result, collector);
                }
                if (caseExpr.ElseResult != null) CollectColumnReferences(caseExpr.ElseResult, collector);
                break;
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments) CollectColumnReferences(arg, collector);
                if (fn.Filter != null) CollectColumnReferences(fn.Filter, collector);
                break;
            case CastExpression cast:
                CollectColumnReferences(cast.Operand, collector);
                break;
            case IsDistinctFromExpression distinct:
                CollectColumnReferences(distinct.Left, collector);
                CollectColumnReferences(distinct.Right, collector);
                break;
        }
    }

    private static (ResolvedSourceTable? Table, string ColumnName) ResolveColumn(
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
