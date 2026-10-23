using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA-06: the tree path only splices typed, subquery-free row policies. These helpers build a typed
/// "column op value" policy (SQLite dialect) and the decision that carries it, so tree tests exercise the
/// other behaviour (per-level filters, parameter binding, masks) with a policy the tree path accepts.
/// </summary>
internal static class TypedTreeRowPolicy
{
    private static readonly PolicyPredicateRenderer Renderer = new();

    public static IRowPolicy Where(string column, BinaryOperator op, object value, SqlParameterType type)
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(value, type));
        var expression = new BinaryExpression(PolicyParameterNamer.Column(column), op, parameter);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expression, namer.Values), TargetSqlDialect.Sqlite, Renderer);
    }

    public static IRowPolicy Int(string column, BinaryOperator op, long value) => Where(column, op, value, SqlParameterType.Int64);

    public static IRowPolicy Text(string column, BinaryOperator op, string value) => Where(column, op, value, SqlParameterType.String);

    public static TableAccessDecision Allowed(
        TableIdentifier table,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        IRowPolicy? policy)
    {
        var decision = TableAccessDecision.Allowed(table, columnAccess, hasUnconstrainedColumnAllow: true);
        return policy == null ? decision : decision.WithAdditionalRowPolicy(policy);
    }
}
