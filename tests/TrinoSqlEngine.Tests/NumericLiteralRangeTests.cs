namespace TrinoSqlEngine.Tests;

using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>#343: a numeric literal that no bound decimal can represent is rejected with the documented construct rejection, never an OverflowException.</summary>
[Trait("Category", "SqlGate")]
public class NumericLiteralRangeTests
{
    private static ProbeEnv Env() => new(TargetSqlDialect.PostgreSql);

    [Theory]
    [InlineData("SELECT 1E31")]
    [InlineData("SELECT 1e999")]
    [InlineData("SELECT 1.5E400")]
    [InlineData("SELECT 567890123456789018901234567890")]
    [InlineData("SELECT 79228162514264337593543950336")]
    public void Compile_RejectsNumericLiteralOutsideDecimalRange(string sql)
    {
        var ex = Assert.Throws<AstBuildException>(() => Env().Compile(sql));
        Assert.DoesNotContain("999", ex.Message);
    }

    [Theory]
    [InlineData("SELECT 12345678901234567890")]
    [InlineData("SELECT 79228162514264337593543950335")]
    [InlineData("SELECT 1.5E10")]
    public void Compile_AcceptsNumericLiteralInsideDecimalRange(string sql)
    {
        var compiled = Env().Compile(sql);
        Assert.NotNull(compiled);
    }
}
