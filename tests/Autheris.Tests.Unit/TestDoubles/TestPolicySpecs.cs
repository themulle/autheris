namespace Autheris.Tests.Unit.TestDoubles;

using System.Collections.Frozen;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

internal static class TestPolicySpecs
{
    public static PolicyPredicate RowPolicy(string column, string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new List<SqlIdentifier> { new(column, false) })),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_" + column, SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_" + column] = new(value, SqlParameterType.String) });

    public static MaskSpec Redact(string column, string replacement) => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_" + column, SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_" + column] = new(replacement, SqlParameterType.String) }.ToFrozenDictionary());
}
