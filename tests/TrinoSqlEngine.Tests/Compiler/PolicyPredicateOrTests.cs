using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-X0l: the typed OR-merge of policy predicates (the counterpart of <see cref="PolicyPredicate.And"/>).</summary>
[Trait("Category", "SqlGate")]
public class PolicyPredicateOrTests
{
    private static PolicyPredicate Eq(string column, string parameter, string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(column, true) })),
            BinaryOperator.Equal, new PolicyParameterExpression(parameter, SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { [parameter] = new(value, SqlParameterType.String) });

    [Fact]
    public void Or_KeepsTheParametersOfBothSides()
    {
        var merged = Eq("a", "p1", "x").Or(Eq("b", "p2", "y"));

        Assert.Equal(new[] { "p1", "p2" }, merged.Parameters.Keys.OrderBy(k => k));
        Assert.Equal(new[] { "a", "b" }, merged.ReferencedColumns.OrderBy(k => k));
    }

    [Fact]
    public void Or_IsATypedOrOfParenthesizedOperands()
    {
        var merged = Eq("a", "p1", "x").Or(Eq("b", "p2", "y"));

        var binary = Assert.IsType<BinaryExpression>(merged.Expression);
        Assert.Equal(BinaryOperator.Or, binary.Operator);
        Assert.IsType<ParenthesizedExpression>(binary.Left);
        Assert.IsType<ParenthesizedExpression>(binary.Right);
    }

    [Fact]
    public void Or_DiffersFromAnd_InItsFingerprint()
    {
        var left = Eq("a", "p1", "x");
        var right = Eq("b", "p2", "y");

        Assert.NotEqual(left.And(right).Fingerprint, left.Or(right).Fingerprint);
    }

    [Fact]
    public void Or_SameParameterSameValue_Merges()
    {
        var merged = Eq("a", "p1", "x").Or(Eq("b", "p1", "x"));

        Assert.Single(merged.Parameters);
    }

    [Fact]
    public void Or_SameParameterDifferentValue_Throws() =>
        Assert.Throws<PolicyConflictException>(() => Eq("a", "p1", "x").Or(Eq("b", "p1", "y")));

    [Fact]
    public void Or_NullOperand_Throws() => Assert.Throws<ArgumentNullException>(() => Eq("a", "p1", "x").Or(null!));
}
