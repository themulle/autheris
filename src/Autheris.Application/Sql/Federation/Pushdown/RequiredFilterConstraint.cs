namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>
/// WP-F review (SEC): which columns of a federated table a set of pushed conjuncts really constrain. Mirrors the single-source rule
/// (<c>AstSecurityVisitor.RequiredFilters.IsColumnConstrained</c>) exactly: a top-level conjunct <c>column op value</c> (=, &lt;, &lt;=, &gt;,
/// &gt;=), BETWEEN, IN (list), LIKE and IS NULL with no column reference on the other side; OR only when every branch constrains the column;
/// never <c>&lt;&gt;</c>, NOT, <c>col = col</c>, IS NOT NULL or a subquery; a column reference that more than one table of the query could
/// satisfy (unqualified, or a qualifier shared by two tables) is unconstrained. Merely mentioning a column in a pushed predicate never counts.
/// </summary>
public static class RequiredFilterConstraint
{
    /// <summary>The catalog columns of <paramref name="metadata"/> that at least one of <paramref name="pushedConjuncts"/> constrains.</summary>
    public static IReadOnlyList<string> ConstrainedColumns(
        IEnumerable<Expression> pushedConjuncts,
        TableAccessTarget target,
        TableMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> allTables)
    {
        ArgumentNullException.ThrowIfNull(pushedConjuncts);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(allTables);

        var conjuncts = pushedConjuncts.ToList();
        var result = new List<string>();
        foreach (var column in metadata.Columns)
        {
            if (conjuncts.Any(c => IsColumnConstrained(c, target, metadata, allTables, column.ColumnName)))
            {
                result.Add(column.ColumnName);
            }
        }

        return result;
    }

    internal static bool IsColumnConstrained(Expression expr, TableAccessTarget target, TableMetadata metadata, IReadOnlyList<ResolvedSourceTable> all, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        bool Match(Expression e) => MatchesTargetColumn(e, target, all, col);

        switch (expr)
        {
            case BinaryExpression b when b.Operator == BinaryOperator.And:
                return IsColumnConstrained(b.Left, target, metadata, all, col) || IsColumnConstrained(b.Right, target, metadata, all, col);

            case BinaryExpression b when b.Operator == BinaryOperator.Or:
                return IsColumnConstrained(b.Left, target, metadata, all, col) && IsColumnConstrained(b.Right, target, metadata, all, col);

            case BinaryExpression b when b.Operator is BinaryOperator.Equal or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                                      or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual:
                return (Match(b.Left) && !ReadsColumns(b.Right)) || (Match(b.Right) && !ReadsColumns(b.Left));

            case BetweenExpression bt when !bt.IsNotBetween:
                return Match(bt.Operand) && !ReadsColumns(bt.Lower) && !ReadsColumns(bt.Upper);

            case InListExpression inList when !inList.IsNotIn && inList.Items.Count > 0:
                return Match(inList.Operand) && !inList.Items.Any(ReadsColumns);

            case LikeExpression lk when !lk.IsNotLike:
                return Match(lk.Operand) && !ReadsColumns(lk.Pattern) && (lk.Escape == null || !ReadsColumns(lk.Escape));

            case UnaryExpression u when u.Operator == UnaryOperator.IsNull:
                return Match(u.Operand);

            default:
                return false;
        }
    }

    private static bool MatchesTargetColumn(Expression expr, TableAccessTarget target, IReadOnlyList<ResolvedSourceTable> all, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        if (expr is CastExpression cast)
        {
            return MatchesTargetColumn(cast.Operand, target, all, col);
        }

        return expr is ColumnReference cr && MatchesColumn(cr, target, all, col);
    }

    private static string Exposed(TableAccessTarget t) => string.IsNullOrWhiteSpace(t.Alias) ? t.TableName : t.Alias;

    private static bool MatchesColumn(ColumnReference colRef, TableAccessTarget target, IReadOnlyList<ResolvedSourceTable> all, string col)
    {
        var parts = colRef.Name.Parts;
        if (parts.Count == 0 || !string.Equals(parts[^1].Value, col, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var others = all.Where(o => !o.Target.Equals(target)).ToList();
        if (parts.Count == 1)
        {
            // Fail closed: any other table of the query that has a column of this name makes the reference ambiguous.
            return !others.Any(o => o.Metadata.Columns.Any(c => string.Equals(c.ColumnName, col, StringComparison.OrdinalIgnoreCase)));
        }

        var qualifier = string.Join('.', parts.Take(parts.Count - 1).Select(p => p.Value));
        if (parts.Count == 2)
        {
            return string.Equals(qualifier, Exposed(target), StringComparison.OrdinalIgnoreCase) &&
                   !others.Any(o => string.Equals(Exposed(o.Target), Exposed(target), StringComparison.OrdinalIgnoreCase));
        }

        // schema.table.column reaches only an unaliased table, and only when no other table of the query shares schema.table.
        if (parts.Count == 3 && string.IsNullOrWhiteSpace(target.Alias))
        {
            var schemaTable = $"{target.Schema}.{target.TableName}";
            return string.Equals(qualifier, schemaTable, StringComparison.OrdinalIgnoreCase) &&
                   !others.Any(o => string.Equals($"{o.Target.Schema}.{o.Target.TableName}", schemaTable, StringComparison.OrdinalIgnoreCase));
        }

        // catalog.schema.table.column names exactly one table.
        return parts.Count == 4 && string.IsNullOrWhiteSpace(target.Alias) &&
               string.Equals(qualifier, target.FullName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the expression reads a column or contains a subquery (the single-source rule treats both as "not a constant").</summary>
    private static bool ReadsColumns(Expression e)
    {
        var probe = new Probe();
        try
        {
            probe.Visit(e);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return true;
        }

        return probe.Found;
    }

    private sealed class Probe : SqlAstRewriter
    {
        public bool Found { get; private set; }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            Found = true;
            return node;
        }

        public override SqlNode VisitSelectStatement(SelectStatement node)
        {
            Found = true;
            return node;
        }
    }
}
