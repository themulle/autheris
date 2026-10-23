namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// WP-122-3: Parameterized TableFilterClause AST builder.
/// Allocates @p_pd_<n> parameters with SEC-ADG-02 collision fail-closed defense and pure TrinoSqlFactory generation.
/// </summary>
public static class PushedClauseBuilder
{
    public static TableFilterClause Build(
        IReadOnlyList<SingleTableConjunct> conjuncts,
        TableMetadata metadata,
        IReadOnlyDictionary<string, object?>? clientParameters = null)
    {
        ArgumentNullException.ThrowIfNull(conjuncts);
        ArgumentNullException.ThrowIfNull(metadata);

        if (conjuncts.Count == 0)
        {
            throw new ArgumentException("Cannot build a filter clause with zero conjuncts.", nameof(conjuncts));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structuralPredicates = new List<PushdownPredicateInfo>();
        var conjunctPredicates = new List<RenderedConjunct>();

        int paramIndex = 0;

        foreach (var conjunct in conjuncts)
        {
            foreach (var col in conjunct.Columns)
            {
                referencedColumns.Add(col);
            }

            var rendered = PrepareConjunct(conjunct.Predicate, parameters, ref paramIndex, structuralPredicates, clientParameters);
            conjunctPredicates.Add(rendered);
        }

        string BuildSql(Func<string, string> columnResolver)
        {
            if (conjunctPredicates.Count == 1)
            {
                return conjunctPredicates[0].Renderer(columnResolver);
            }

            return string.Join(" AND ", conjunctPredicates.Select(c => "(" + c.Renderer(columnResolver) + ")"));
        }

        var defaultPredicate = BuildSql(static col => col);

        return new TableFilterClause(
            defaultPredicate,
            parameters,
            referencedColumns.ToList())
        {
            TrinoSqlFactory = columnResolver => BuildSql(columnResolver ?? (static col => col)),
            PushdownPredicates = structuralPredicates
        };
    }

    private sealed record RenderedConjunct(Func<Func<string, string>, string> Renderer);

    private static RenderedConjunct PrepareConjunct(
        Expression expr,
        Dictionary<string, object?> parameters,
        ref int paramIndex,
        List<PushdownPredicateInfo> structuralPredicates,
        IReadOnlyDictionary<string, object?>? clientParameters)
    {
        var renderer = BuildRenderer(expr, parameters, ref paramIndex, structuralPredicates, clientParameters);
        return new RenderedConjunct(renderer);
    }

    private static Func<Func<string, string>, string> BuildRenderer(
        SqlNode node,
        Dictionary<string, object?> parameters,
        ref int paramIndex,
        List<PushdownPredicateInfo> structuralPredicates,
        IReadOnlyDictionary<string, object?>? clientParameters)
    {
        switch (node)
        {
            case ColumnReference col:
            {
                var colName = col.Name.Parts[^1].Value;
                return resolve => DataApiFilterTranslator.QuoteIdentifier(resolve(colName));
            }

            case LiteralExpression lit:
            {
                var marker = AllocateParameter(parameters, ref paramIndex, lit.Value);
                return _ => marker;
            }

            case ParameterReference:
            case NamedBindMarkerExpression:
            {
                // SQLA-09: bind the value the client supplied; NULL is never bound. An unresolvable parameter means the conjunct must not be pushed.
                if (!PushdownParameterResolver.TryResolve(node, clientParameters, out var clientValue))
                {
                    throw new PushdownRejectedException();
                }

                var marker = AllocateParameter(parameters, ref paramIndex, clientValue);
                return _ => marker;
            }

            case ParenthesizedExpression paren:
            {
                var inner = BuildRenderer(paren.Expression, parameters, ref paramIndex, structuralPredicates, clientParameters);
                return resolve => $"({inner(resolve)})";
            }

            case BinaryExpression binary:
            {
                if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
                {
                    var opStr = binary.Operator == BinaryOperator.And ? "AND" : "OR";
                    var leftRenderer = BuildRenderer(binary.Left, parameters, ref paramIndex, structuralPredicates, clientParameters);
                    var rightRenderer = BuildRenderer(binary.Right, parameters, ref paramIndex, structuralPredicates, clientParameters);
                    return resolve => $"({leftRenderer(resolve)} {opStr} {rightRenderer(resolve)})";
                }

                var opString = binary.Operator switch
                {
                    BinaryOperator.Equal => "=",
                    BinaryOperator.NotEqual => "<>",
                    BinaryOperator.LessThan => "<",
                    BinaryOperator.LessThanOrEqual => "<=",
                    BinaryOperator.GreaterThan => ">",
                    BinaryOperator.GreaterThanOrEqual => ">=",
                    _ => throw new NotSupportedException($"Binary operator '{binary.Operator}' is not supported for pushdown.")
                };

                var col = ExtractColumnName(binary.Left) ?? ExtractColumnName(binary.Right);
                if (col != null)
                {
                    structuralPredicates.Add(new PushdownPredicateInfo(col, binary.Operator.ToString()));
                }

                var leftR = BuildRenderer(binary.Left, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var rightR = BuildRenderer(binary.Right, parameters, ref paramIndex, structuralPredicates, clientParameters);
                return resolve => $"{leftR(resolve)} {opString} {rightR(resolve)}";
            }

            case UnaryExpression unary:
            {
                var col = ExtractColumnName(unary.Operand);
                if (unary.Operator == UnaryOperator.IsNull)
                {
                    if (col != null) structuralPredicates.Add(new PushdownPredicateInfo(col, "IsNull"));
                    var opR = BuildRenderer(unary.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                    return resolve => $"{opR(resolve)} IS NULL";
                }

                if (unary.Operator == UnaryOperator.IsNotNull)
                {
                    if (col != null) structuralPredicates.Add(new PushdownPredicateInfo(col, "IsNotNull"));
                    var opR = BuildRenderer(unary.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                    return resolve => $"{opR(resolve)} IS NOT NULL";
                }

                if (unary.Operator == UnaryOperator.Not)
                {
                    var opR = BuildRenderer(unary.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                    return resolve => $"NOT ({opR(resolve)})";
                }

                throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported for pushdown.");
            }

            case InListExpression inList:
            {
                var col = ExtractColumnName(inList.Operand);
                if (col != null)
                {
                    structuralPredicates.Add(new PushdownPredicateInfo(col, inList.IsNotIn ? "NotIn" : "In"));
                }

                var operandR = BuildRenderer(inList.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var itemRenderers = new List<Func<Func<string, string>, string>>(inList.Items.Count);
                foreach (var item in inList.Items)
                {
                    itemRenderers.Add(BuildRenderer(item, parameters, ref paramIndex, structuralPredicates, clientParameters));
                }
                var opStr = inList.IsNotIn ? "NOT IN" : "IN";

                return resolve =>
                {
                    var opSql = operandR(resolve);
                    var itemsSql = string.Join(", ", itemRenderers.Select(i => i(resolve)));
                    return $"{opSql} {opStr} ({itemsSql})";
                };
            }

            case BetweenExpression between:
            {
                var col = ExtractColumnName(between.Operand);
                if (col != null)
                {
                    structuralPredicates.Add(new PushdownPredicateInfo(col, between.IsNotBetween ? "NotBetween" : "Between"));
                }

                var operandR = BuildRenderer(between.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var lowerR = BuildRenderer(between.Lower, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var upperR = BuildRenderer(between.Upper, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var opStr = between.IsNotBetween ? "NOT BETWEEN" : "BETWEEN";

                return resolve => $"{operandR(resolve)} {opStr} {lowerR(resolve)} AND {upperR(resolve)}";
            }

            case LikeExpression like:
            {
                var col = ExtractColumnName(like.Operand);
                if (col != null)
                {
                    structuralPredicates.Add(new PushdownPredicateInfo(col, like.IsNotLike ? "NotLike" : "Like"));
                }

                var operandR = BuildRenderer(like.Operand, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var patR = BuildRenderer(like.Pattern, parameters, ref paramIndex, structuralPredicates, clientParameters);
                var escapeR = like.Escape != null ? BuildRenderer(like.Escape, parameters, ref paramIndex, structuralPredicates, clientParameters) : null;
                var opStr = like.IsNotLike ? "NOT LIKE" : "LIKE";

                return resolve =>
                {
                    var opSql = operandR(resolve);
                    var patSql = patR(resolve);
                    var escSql = escapeR != null ? $" ESCAPE {escapeR(resolve)}" : string.Empty;
                    return $"{opSql} {opStr} {patSql}{escSql}";
                };
            }

            default:
                throw new NotSupportedException($"Expression type '{node.GetType().Name}' is not supported for pushdown.");
        }
    }

    private static string AllocateParameter(Dictionary<string, object?> parameters, ref int index, object? value)
    {
        var marker = "@p_pd_" + (index++).ToString(CultureInfo.InvariantCulture);
        // SEC-ADG-02 / SEC-DATA122-01: Duplicate parameter names must fail closed immediately.
        if (!parameters.TryAdd(marker, value))
        {
            throw new SecurityException($"Duplicate pushdown parameter name '{marker}' encountered (fail-closed).");
        }
        return marker;
    }

    private static string? ExtractColumnName(SqlNode node)
    {
        while (node is ParenthesizedExpression p) node = p.Expression;
        if (node is ColumnReference col)
        {
            return col.Name.Parts[^1].Value;
        }

        return null;
    }
}
