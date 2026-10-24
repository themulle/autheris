namespace TrinoSqlEngine.Tests;

using System.Threading;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Tests.Compiler;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>#343: a numeric literal that no bound decimal can represent is rejected with the documented construct rejection, never an OverflowException.</summary>
[Trait("Category", "SqlGate")]
public class NumericLiteralRangeTests
{
    private const string FixedMessage = "Numeric literal is outside the supported decimal range.";

    private static ProbeEnv Env() => new(TargetSqlDialect.PostgreSql);

    [Theory]
    [InlineData("SELECT 1E31")]
    [InlineData("SELECT 1e999")]
    [InlineData("SELECT 1.5E400")]
    [InlineData("SELECT 567890123456789018901234567890")]
    [InlineData("SELECT 79228162514264337593543950336")]
    [InlineData("SELECT -1E31")]
    [Trait("Category", "Security")]
    public void Compile_RejectsNumericLiteralOutsideDecimalRange(string sql)
    {
        var ex = Assert.Throws<AstBuildException>(() => Env().Compile(sql));
        Assert.Equal(FixedMessage, ex.Message);
    }

    // Underflow is intended: a literal below the smallest decimal magnitude rounds to zero and is accepted (it carries no overflow).
    [Theory]
    [InlineData("SELECT 12345678901234567890")]
    [InlineData("SELECT 79228162514264337593543950335")]
    [InlineData("SELECT -79228162514264337593543950335")]
    [InlineData("SELECT 1.5E10")]
    [InlineData("SELECT 1e-999")]
    [InlineData("SELECT 0.0000000000000000000000000000001")]
    public void Compile_AcceptsNumericLiteralInsideDecimalRange(string sql)
    {
        var compiled = Env().Compile(sql);
        Assert.NotNull(compiled);
    }

    private static SelectStatement WithLiteral(object value)
    {
        var select = (SelectStatement)CompilerTestHelpers.Build("SELECT 1.5 FROM dbo.Events e");
        var spec = (QuerySpecification)select.Body;
        var item = (ColumnSelectItem)spec.Projections[0];
        return select with { Body = spec with { Projections = [item with { Expression = new LiteralExpression(value, LiteralType.Decimal) }] } };
    }

    [Theory]
    [InlineData("1e999")]
    [InlineData("12")]
    [InlineData(1.5)]
    [Trait("Category", "Security")]
    public void Generator_RefusesADecimalLiteralThatIsNotADecimal(object value)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.PostgreSql);
        Assert.Throws<SqlCompileNotSupportedException>(() => generator.Generate(WithLiteral(value), ParameterSource.Empty, CancellationToken.None));
        Assert.Throws<SqlCompileNotSupportedException>(() => generator.GenerateSql(WithLiteral(value)));
    }
}
