namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>One literal of a point lookup as written in the query (the type matters for the key conversion).</summary>
public readonly record struct PointLiteral(object Value, LiteralType Type);

/// <summary>
/// F-GOV-14 (WI-7, M6, SEC-21): finds the point lookups on a protected table in a user query. A lookup only ever
/// narrows what is fetched, so eligibility is strict and everything else is a scan (always safe):
/// <list type="bullet">
/// <item>a single <see cref="QuerySpecification"/> without <c>WITH</c>, set operation, sub-query, lateral or derived table;</item>
/// <item>the protected table occurs exactly once;</item>
/// <item>only the top-level <c>AND</c> chain of <c>WHERE</c> counts: <c>col = literal</c>, <c>literal = col</c>,
/// <c>col IN (literal, ...)</c> (never <c>NOT IN</c>, <c>OR</c>, expressions);</item>
/// <item>a column counts when it is qualified with the table's alias (or name when it has no alias), or when it is
/// unqualified and the FROM clause has exactly one named table.</item>
/// </list>
/// </summary>
public static class VirtualFilterPointLookupExtractor
{
    /// <returns>
    /// Literal values per column (as written, first predicate per column wins), an empty map when the query is eligible but
    /// has no predicate, or null when the query shape is not eligible.
    /// </returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<PointLiteral>>? Extract(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        TableIdentifier table)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (statement is not SelectStatement { With: null, Body: QuerySpecification spec } select)
        {
            return null;
        }

        if (!IsSimpleFrom(spec.From, out var namedTables) || ContainsSubquery(select))
        {
            return null;
        }

        var matches = tables.Where(t => t.Metadata.Identifier == table).ToList();
        if (matches.Count != 1)
        {
            return null;   // not in the query, or a self-join
        }

        var target = matches[0].Target;
        var qualifiers = Qualifiers(target, table);
        var result = new Dictionary<string, IReadOnlyList<PointLiteral>>(StringComparer.OrdinalIgnoreCase);
        if (spec.Where != null)
        {
            Collect(spec.Where, qualifiers, namedTables == 1, result);
        }

        return result;
    }

    private static HashSet<string> Qualifiers(TrinoSqlEngine.Analysis.TableAccessTarget target, TableIdentifier table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target.Alias))
        {
            names.Add(target.Alias);   // with an alias, only the alias qualifies a column
            return names;
        }

        names.Add(target.TableName);
        names.Add(table.TableName);
        names.Add($"{table.Schema}.{table.TableName}");
        names.Add(table.ToString());
        if (!string.IsNullOrWhiteSpace(target.FullName))
        {
            names.Add(target.FullName);
        }

        return names;
    }

    private static bool IsSimpleFrom(TableSource? from, out int namedTables)
    {
        namedTables = 0;
        switch (from)
        {
            case NamedTableSource:
                namedTables = 1;
                return true;
            case JoinedTableSource joined:
                if (!IsSimpleFrom(joined.Left, out var left) || !IsSimpleFrom(joined.Right, out var right))
                {
                    return false;
                }

                namedTables = left + right;
                return true;
            default:
                return false;   // derived table, lateral, or no FROM
        }
    }

    private static bool ContainsSubquery(SelectStatement statement)
    {
        var detector = new SubqueryDetector();
        try
        {
            detector.Visit(statement);
        }
        catch (NotSupportedException)
        {
            return true;   // an AST node this walk does not know: not eligible
        }

        return detector.Found;
    }

    private static void Collect(Expression expression, HashSet<string> qualifiers, bool allowUnqualified, Dictionary<string, IReadOnlyList<PointLiteral>> result)
    {
        switch (expression)
        {
            case ParenthesizedExpression paren:
                Collect(paren.Expression, qualifiers, allowUnqualified, result);
                break;
            case BinaryExpression { Operator: BinaryOperator.And } and:
                Collect(and.Left, qualifiers, allowUnqualified, result);
                Collect(and.Right, qualifiers, allowUnqualified, result);
                break;
            case BinaryExpression { Operator: BinaryOperator.Equal } eq:
                if (eq.Left is ColumnReference lc && Literal(eq.Right) is { } rl)
                {
                    Add(result, lc, [rl], qualifiers, allowUnqualified);
                }
                else if (eq.Right is ColumnReference rc && Literal(eq.Left) is { } ll)
                {
                    Add(result, rc, [ll], qualifiers, allowUnqualified);
                }

                break;
            case InListExpression { IsNotIn: false, Operand: ColumnReference column } list:
                var values = new List<PointLiteral>(list.Items.Count);
                foreach (var item in list.Items)
                {
                    if (Literal(item) is not { } literal)
                    {
                        return;   // one non-literal item: the list is not a point lookup
                    }

                    values.Add(literal);
                }

                if (values.Count > 0)
                {
                    Add(result, column, values, qualifiers, allowUnqualified);
                }

                break;
        }
    }

    private static PointLiteral? Literal(Expression expression)
    {
        while (expression is ParenthesizedExpression paren)
        {
            expression = paren.Expression;
        }

        return expression is LiteralExpression { Value: not null } literal &&
               literal.Type is LiteralType.Integer or LiteralType.String or LiteralType.Decimal
            ? new PointLiteral(literal.Value, literal.Type)
            : null;
    }

    private static void Add(
        Dictionary<string, IReadOnlyList<PointLiteral>> result,
        ColumnReference column,
        IReadOnlyList<PointLiteral> values,
        HashSet<string> qualifiers,
        bool allowUnqualified)
    {
        var parts = column.Name.Parts;
        var name = parts[^1].Value;
        bool qualifiedForTable = parts.Count == 1
            ? allowUnqualified
            : qualifiers.Contains(string.Join(".", parts.Take(parts.Count - 1).Select(p => p.Value)));
        if (qualifiedForTable)
        {
            result.TryAdd(name, values);
        }
    }

    private sealed class SubqueryDetector : SqlAstRewriter
    {
        public bool Found { get; private set; }

        public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node) => Mark(node);

        public override SqlNode VisitExistsExpression(ExistsExpression node) => Mark(node);

        public override SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node) => Mark(node);

        public override SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node) => Mark(node);

        public override SqlNode VisitSubqueryTableSource(SubqueryTableSource node) => Mark(node);

        public override SqlNode VisitLateralTableSource(LateralTableSource node) => Mark(node);

        private SqlNode Mark(SqlNode node)
        {
            Found = true;
            return node;
        }
    }
}
