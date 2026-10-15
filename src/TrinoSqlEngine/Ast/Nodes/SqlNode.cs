namespace TrinoSqlEngine.Ast.Nodes;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Abstract base record of all AST nodes.
/// </summary>
public abstract record SqlNode
{
    private static readonly ConcurrentDictionary<Type, Func<SqlNode, IEnumerable<SqlNode>>> ChildEnumerators = new();

    /// <summary>
    /// Enumerates direct <see cref="SqlNode"/> children without per-node reflection.
    /// </summary>
    public virtual IEnumerable<SqlNode> EnumerateChildren() => EnumerateChildrenFast(this);

    private static IEnumerable<SqlNode> EnumerateChildrenFast(SqlNode node)
    {
        switch (node)
        {
            case LiteralExpression
                or SqlIdentifier
                or ParameterReference
                or PolicyParameterExpression
                or NamedBindMarkerExpression
                or CurrentDateTimeExpression
                or TypedLiteralExpression
                or IntervalLiteralExpression
                or FrameBound:
                return [];
            case SqlQualifiedName qn:
                return qn.Parts;
            case ColumnReference col:
                return [col.Name];
            case ParenthesizedExpression p:
                return [p.Expression];
            case UnaryExpression u:
                return [u.Operand];
            case CastExpression c:
                return [c.Operand];
            case BinaryExpression b:
                return [b.Left, b.Right];
            case IsDistinctFromExpression d:
                return [d.Left, d.Right];
            case SubscriptExpression s:
                return [s.Target, s.Index];
            case PositionExpression pos:
                return [pos.Needle, pos.Haystack];
            case ExtractExpression ext:
                return [ext.Source];
            case DateFunctionExpression dt:
                return [dt.Source];
            case BetweenExpression bt:
                return [bt.Operand, bt.Lower, bt.Upper];
            case LikeExpression lk:
                return lk.Escape is not null ? [lk.Operand, lk.Pattern, lk.Escape] : [lk.Operand, lk.Pattern];
            case SubstringExpression sub:
                return sub.Length is not null ? [sub.Source, sub.Start, sub.Length] : [sub.Source, sub.Start];
            case TrimExpression tr:
                return tr.Characters is not null ? [tr.Source, tr.Characters] : [tr.Source];
            case RowValueExpression row:
                return row.Elements;
            case ArrayConstructorExpression arr:
                return arr.Elements;
            case ExistsExpression ex:
                return [ex.Subquery];
            case ScalarSubqueryExpression sc:
                return [sc.Subquery];
            case InSubqueryExpression inSq:
                return [inSq.Operand, inSq.Subquery];
            case QuantifiedComparisonExpression qc:
                return [qc.Left, qc.Subquery];
            case SecurityPredicateExpression sp:
                return [sp.Predicate];
            case MaskExpression m:
                return [m.Column];
            case WhenClause w:
                return [w.Condition, w.Result];
            case OrderByClause ob:
                return ob.Elements;
            case OrderByElement obe:
                return [obe.Expression];
            case OnJoinCondition on:
                return [on.Predicate];
            case UsingJoinCondition ujc:
                return ujc.Columns;
            case TableQueryBody tqb:
                return [tqb.TableName];
            case ValuesQueryBody vqb:
                return vqb.Rows;
            case SetOperationQuery soq:
                return [soq.Left, soq.Right];
            case UpdateAssignment ua:
                return [ua.Column, ua.Value];
            case WithClause wc:
                return wc.Ctes;
            default:
                return ChildEnumerators.GetOrAdd(node.GetType(), BuildCompiledEnumerator)(node);
        }
    }

    private static Func<SqlNode, IEnumerable<SqlNode>> BuildCompiledEnumerator(Type type)
    {
        var getters = Array.ConvertAll(
            Array.FindAll(
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                static p => p.GetIndexParameters().Length == 0 &&
                            p.GetMethod is not null &&
                            !p.PropertyType.IsPrimitive &&
                            !p.PropertyType.IsEnum &&
                            p.PropertyType != typeof(string)),
            PropertyGetters.Create);

        if (getters.Length == 0)
        {
            return static _ => [];
        }

        return target => EnumerateViaGetters(target, getters);
    }

    private static IEnumerable<SqlNode> EnumerateViaGetters(SqlNode target, Func<object, object?>[] getters)
    {
        for (int i = 0; i < getters.Length; i++)
        {
            var value = getters[i](target);
            if (value is SqlNode child)
            {
                yield return child;
            }
            else if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is SqlNode direct)
                    {
                        yield return direct;
                    }
                    else if (item is IEnumerable nested && item is not string)
                    {
                        foreach (var inner in nested)
                        {
                            if (inner is SqlNode innerChild)
                            {
                                yield return innerChild;
                            }
                        }
                    }
                }
            }
        }
    }
}
