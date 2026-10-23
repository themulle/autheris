namespace Autheris.Application.Sql.Federation.Pushdown;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Nodes;

public static class RejectionReason
{
    public const string MaskedColumn = "MASKED_COLUMN";
    public const string MultiTable = "MULTI_TABLE";
    public const string UnsupportedExpression = "UNSUPPORTED_EXPRESSION";
    public const string CollationRisk = "COLLATION_RISK";
    public const string NotSqlSource = "NOT_SQL_SOURCE";
    public const string GatewayEnforcedTable = "GATEWAY_ENFORCED_TABLE";
    public const string NullInNotInList = "NULL_IN_NOT_IN_LIST";
    public const string MaxInListExceeded = "MAX_IN_LIST_EXCEEDED";
    public const string UnresolvedParameter = "UNRESOLVED_PARAMETER";
}

public sealed record PushdownEvaluationResult(
    bool IsPushable,
    string? RejectionReason = null)
{
    public static PushdownEvaluationResult Allowed() => new(true);
    public static PushdownEvaluationResult Rejected(string reason) => new(false, reason);
}

/// <summary>
/// WP-122-2: Evaluates pushdown eligibility against dialect capability matrices and Zero-Trust security invariants.
/// </summary>
public static class PushabilityEvaluator
{
    public static PushdownEvaluationResult Evaluate(
        SingleTableConjunct conjunct,
        TableMetadata metadata,
        TableAccessDecision decision,
        DatabaseDialect dialect,
        VirtualFilterTablePlan? vfPlan,
        PredicatePushdownOptions options,
        IReadOnlyDictionary<string, object?>? clientParameters = null)
    {
        ArgumentNullException.ThrowIfNull(conjunct);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(options);

        // SEC-DATA122-05: Only relational SQL sources support pushdown. Declarative HTTP sources fail closed.
        if (metadata.Table.DataSourceType != DataSourceType.Sql)
        {
            return PushdownEvaluationResult.Rejected(RejectionReason.NotSqlSource);
        }

        // SEC-FED-23: Tables undergoing gateway-side virtual filter row removal are exempted to avoid count oracles.
        if (vfPlan is { Enforcement: not null, SkipUpstream: false })
        {
            return PushdownEvaluationResult.Rejected(RejectionReason.GatewayEnforcedTable);
        }

        // SEC-DATA122-02 (SEC-FED-19): Every referenced column must have Clear access.
        // Columns with Mask, MaskWithHmac, or Deny must NEVER be pushed down.
        foreach (var colName in conjunct.Columns)
        {
            var level = decision.GetEffectiveColumnAccess(colName, metadata);
            if (level != ColumnAccessLevel.Clear)
            {
                return PushdownEvaluationResult.Rejected(RejectionReason.MaskedColumn);
            }
        }

        // Evaluate AST expression safety
        return ValidateExpression(conjunct.Predicate, metadata, dialect, options, clientParameters, negated: false);
    }

    private static PushdownEvaluationResult ValidateExpression(
        SqlNode node,
        TableMetadata metadata,
        DatabaseDialect dialect,
        PredicatePushdownOptions options,
        IReadOnlyDictionary<string, object?>? clientParameters,
        bool negated)
    {
        switch (node)
        {
            case ColumnReference:
            case LiteralExpression:
                return PushdownEvaluationResult.Allowed();

            case ParameterReference:
            case NamedBindMarkerExpression:
                // SQLA-09: a parameter is pushed only when its value can be bound from the client values (never as NULL).
                return PushdownParameterResolver.TryResolve(node, clientParameters, out _)
                    ? PushdownEvaluationResult.Allowed()
                    : PushdownEvaluationResult.Rejected(RejectionReason.UnresolvedParameter);

            case ParenthesizedExpression paren:
                return ValidateExpression(paren.Expression, metadata, dialect, options, clientParameters, negated);

            case BinaryExpression binary:
            {
                if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
                {
                    var leftRes = ValidateExpression(binary.Left, metadata, dialect, options, clientParameters, negated);
                    if (!leftRes.IsPushable) return leftRes;
                    return ValidateExpression(binary.Right, metadata, dialect, options, clientParameters, negated);
                }

                if (binary.Operator is BinaryOperator.Equal or BinaryOperator.LessThan or
                    BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
                {
                    // SQLA-10: under NOT every comparison is a negative filter; a string range comparison is collation sensitive even without NOT.
                    bool isRange = binary.Operator != BinaryOperator.Equal;
                    if (negated || isRange)
                    {
                        var rangeColName = ExtractColumnName(binary.Left) ?? ExtractColumnName(binary.Right);
                        var rangeCol = rangeColName != null ? metadata.GetColumn(rangeColName) : null;
                        if (!PushabilityMatrix.IsNegativeFilterSafeForDialect(dialect, rangeCol))
                        {
                            return PushdownEvaluationResult.Rejected(RejectionReason.CollationRisk);
                        }
                    }

                    var leftRes = ValidateExpression(binary.Left, metadata, dialect, options, clientParameters, negated);
                    if (!leftRes.IsPushable) return leftRes;
                    return ValidateExpression(binary.Right, metadata, dialect, options, clientParameters, negated);
                }

                if (binary.Operator is BinaryOperator.NotEqual)
                {
                    var colName = ExtractColumnName(binary.Left) ?? ExtractColumnName(binary.Right);
                    var col = colName != null ? metadata.GetColumn(colName) : null;
                    if (!PushabilityMatrix.IsNegativeFilterSafeForDialect(dialect, col))
                    {
                        return PushdownEvaluationResult.Rejected(RejectionReason.CollationRisk);
                    }

                    var leftRes = ValidateExpression(binary.Left, metadata, dialect, options, clientParameters, negated);
                    if (!leftRes.IsPushable) return leftRes;
                    return ValidateExpression(binary.Right, metadata, dialect, options, clientParameters, negated);
                }

                return PushdownEvaluationResult.Rejected(RejectionReason.UnsupportedExpression);
            }

            case UnaryExpression unary:
            {
                if (unary.Operator is UnaryOperator.IsNull or UnaryOperator.IsNotNull)
                {
                    return ValidateExpression(unary.Operand, metadata, dialect, options, clientParameters, negated);
                }

                if (unary.Operator is UnaryOperator.Not)
                {
                    // SQLA-10: NOT turns a CI-collation superset into a subset; every comparison below it is checked as a negative filter.
                    return ValidateExpression(unary.Operand, metadata, dialect, options, clientParameters, negated: true);
                }

                return PushdownEvaluationResult.Rejected(RejectionReason.UnsupportedExpression);
            }

            case InListExpression inList:
            {
                if (inList.Items.Count > options.MaxInListCount)
                {
                    return PushdownEvaluationResult.Rejected(RejectionReason.MaxInListExceeded);
                }

                if (PushabilityMatrix.HasNullInNotInList(inList))
                {
                    return PushdownEvaluationResult.Rejected(RejectionReason.NullInNotInList);
                }

                if (inList.IsNotIn || negated)
                {
                    var colName = ExtractColumnName(inList.Operand);
                    var col = colName != null ? metadata.GetColumn(colName) : null;
                    if (!PushabilityMatrix.IsNegativeFilterSafeForDialect(dialect, col))
                    {
                        return PushdownEvaluationResult.Rejected(RejectionReason.CollationRisk);
                    }
                }

                var operandRes = ValidateExpression(inList.Operand, metadata, dialect, options, clientParameters, negated);
                if (!operandRes.IsPushable) return operandRes;

                foreach (var item in inList.Items)
                {
                    var itemRes = ValidateExpression(item, metadata, dialect, options, clientParameters, negated);
                    if (!itemRes.IsPushable) return itemRes;
                }

                return PushdownEvaluationResult.Allowed();
            }

            case BetweenExpression between:
            {
                // SQLA-10: BETWEEN is a pair of string range comparisons; it needs the same collation guarantee as a negative filter.
                {
                    var colName = ExtractColumnName(between.Operand);
                    var col = colName != null ? metadata.GetColumn(colName) : null;
                    if (!PushabilityMatrix.IsNegativeFilterSafeForDialect(dialect, col))
                    {
                        return PushdownEvaluationResult.Rejected(RejectionReason.CollationRisk);
                    }
                }

                var opRes = ValidateExpression(between.Operand, metadata, dialect, options, clientParameters, negated);
                if (!opRes.IsPushable) return opRes;
                var lowRes = ValidateExpression(between.Lower, metadata, dialect, options, clientParameters, negated);
                if (!lowRes.IsPushable) return lowRes;
                return ValidateExpression(between.Upper, metadata, dialect, options, clientParameters, negated);
            }

            case LikeExpression like:
            {
                if (PushabilityMatrix.HasDialectSpecificBrackets(like, dialect))
                {
                    return PushdownEvaluationResult.Rejected(RejectionReason.UnsupportedExpression);
                }

                if (like.IsNotLike || negated)
                {
                    var colName = ExtractColumnName(like.Operand);
                    var col = colName != null ? metadata.GetColumn(colName) : null;
                    if (!PushabilityMatrix.IsNegativeFilterSafeForDialect(dialect, col))
                    {
                        return PushdownEvaluationResult.Rejected(RejectionReason.CollationRisk);
                    }
                }

                var opRes = ValidateExpression(like.Operand, metadata, dialect, options, clientParameters, negated);
                if (!opRes.IsPushable) return opRes;
                var patRes = ValidateExpression(like.Pattern, metadata, dialect, options, clientParameters, negated);
                if (!patRes.IsPushable) return patRes;
                if (like.Escape != null)
                {
                    return ValidateExpression(like.Escape, metadata, dialect, options, clientParameters, negated);
                }

                return PushdownEvaluationResult.Allowed();
            }

            default:
                // Subqueries, functions, casts, arithmetic are unsupported for pushdown.
                return PushdownEvaluationResult.Rejected(RejectionReason.UnsupportedExpression);
        }
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
