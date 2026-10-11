using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// CR-ADG-04: the binary-exact tenant comparison is asserted on the node structure, not on SQL text. A mutant that joins
/// conjuncts with OR, or compares the column with itself, changes the structure and fails here (no container needed).
/// </summary>
public class TenantPredicateStructureTests
{
    private const string Column = "TenantId";
    private const string Parameter = "__autheris_tenant";

    public static IEnumerable<object[]> Dialects() => new[]
    {
        (TargetSqlDialect.SqlServer, 3), (TargetSqlDialect.PostgreSql, 2), (TargetSqlDialect.DuckDb, 2),
        (TargetSqlDialect.Oracle, 2), (TargetSqlDialect.Databricks, 1)
    }.Select(d => new object[] { d.Item1, d.Item2 });

    private static void Conjuncts(Expression e, List<BinaryExpression> into)
    {
        switch (e)
        {
            case ParenthesizedExpression p:
                Conjuncts(p.Expression, into);
                break;
            case BinaryExpression { Operator: BinaryOperator.And } and:
                Conjuncts(and.Left, into);
                Conjuncts(and.Right, into);
                break;
            case BinaryExpression { Operator: BinaryOperator.Equal } eq:
                into.Add(eq);
                break;
            default:
                Assert.Fail($"The tenant predicate may only combine equalities with AND, found {e.GetType().Name}");
                break;
        }
    }

    /// <summary>Operand shape with the column and the parameter both rendered as "@": casts and function calls keep their identity.</summary>
    private static string Shape(Expression e) => e switch
    {
        ColumnReference or PolicyParameterExpression => "@",
        CastExpression c => $"cast({Shape(c.Operand)} as {c.TargetType})",
        FunctionCallExpression f => $"{f.Name.SimpleName}({string.Join(",", f.Arguments.Select(Shape))})",
        _ => Assert.IsType<ColumnReference>(e).ToString()!   // anything else is a structure violation
    };

    private static (int Columns, int Parameters) Leaves(Expression e)
    {
        int columns = 0, parameters = 0;
        AstReflection.Walk(e, node =>
        {
            if (node is ColumnReference reference)
            {
                Assert.Equal(Column, reference.Name.SimpleName);
                columns++;
            }
            else if (node is PolicyParameterExpression parameter)
            {
                Assert.Equal(Parameter, parameter.Name);
                Assert.Equal(ParameterOrigin.Tenant, parameter.Origin);
                parameters++;
            }

            return true;
        });
        return (columns, parameters);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TenantPredicate_IsAnAndOfEqualities_EachComparingTheColumnWithTheParameter(TargetSqlDialect dialect, int expectedConjuncts)
    {
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(dialect), Column, Parameter, SqlParameterType.String);
        var conjuncts = new List<BinaryExpression>();
        Conjuncts(predicate, conjuncts);

        Assert.Equal(expectedConjuncts, conjuncts.Count);
        foreach (var conjunct in conjuncts)
        {
            // Left side: only the column. Right side: only the tenant parameter. Never the column against itself.
            Assert.Equal((1, 0), Leaves(conjunct.Left));
            Assert.Equal((0, 1), Leaves(conjunct.Right));
            Assert.Equal(Shape(conjunct.Left), Shape(conjunct.Right));
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void TenantPredicate_NeverContainsALiteralOrADisjunction(TargetSqlDialect dialect, int expectedConjuncts)
    {
        _ = expectedConjuncts;
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(dialect), Column, Parameter, SqlParameterType.String);
        AstReflection.Walk(predicate, node =>
        {
            Assert.IsNotType<LiteralExpression>(node);
            if (node is BinaryExpression b) Assert.True(b.Operator is BinaryOperator.And or BinaryOperator.Equal, $"unexpected operator {b.Operator}");
            return true;
        });
    }

    [Fact]
    public void SqlServer_HasThePlainSeekConjunct_TheBinaryConjunct_AndTheLengthConjunct()
    {
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer), Column, Parameter, SqlParameterType.String);
        var conjuncts = new List<BinaryExpression>();
        Conjuncts(predicate, conjuncts);

        Assert.Equal("@", Shape(conjuncts[0].Left));                                      // col = @t keeps the index seek
        Assert.Equal("cast(cast(@ as varchar) as varbinary)", Shape(conjuncts[1].Left));  // byte comparison
        Assert.Equal("DATALENGTH(cast(@ as varchar))", Shape(conjuncts[2].Left));         // closes the varbinary zero padding gap
    }

    [Fact]
    public void Databricks_HasOnlyTheBinaryConjunct_NoPlainEquality()
    {
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks), Column, Parameter, SqlParameterType.String);
        var conjuncts = new List<BinaryExpression>();
        Conjuncts(predicate, conjuncts);

        var only = Assert.Single(conjuncts);
        Assert.Equal("cast(@ as varbinary)", Shape(only.Left));
    }

    [Theory]
    [InlineData("UTF8_BINARY")]
    [InlineData("utf8_binary")]
    public void Databricks_Utf8BinaryColumn_UsesOnlyThePlainEquality(string collation)
    {
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks), Column, Parameter, SqlParameterType.String, null, collation);
        var conjuncts = new List<BinaryExpression>();
        Conjuncts(predicate, conjuncts);

        var only = Assert.Single(conjuncts);
        Assert.Equal("@", Shape(only.Left));     // col = :t, exact and sargable on UTF8_BINARY
        Assert.Equal("@", Shape(only.Right));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UTF8_LCASE")]
    [InlineData("UNICODE_CI")]
    [InlineData("UTF8_BINARY_LIKE")]
    public void Databricks_UnknownOrCollatedColumn_KeepsTheBinaryComparison(string? collation)
    {
        var predicate = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks), Column, Parameter, SqlParameterType.String, null, collation);
        var conjuncts = new List<BinaryExpression>();
        Conjuncts(predicate, conjuncts);

        var only = Assert.Single(conjuncts);
        Assert.Equal("cast(@ as varbinary)", Shape(only.Left));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void OtherDialects_IgnoreTheCollation_AndAlwaysCompareBinaryExact(TargetSqlDialect dialect)
    {
        var withBinary = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(dialect), Column, Parameter, SqlParameterType.String, null, "UTF8_BINARY");
        var without = TenantPredicateFactory.Build(DialectCapabilityTable.Default.Get(dialect), Column, Parameter, SqlParameterType.String);
        Assert.Equal(AstReflection.Fingerprint(without), AstReflection.Fingerprint(withBinary));
    }
}
