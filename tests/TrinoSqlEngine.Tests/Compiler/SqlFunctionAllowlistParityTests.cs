using TrinoSqlEngine;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-A (GAP-019, GAP-022, GAP-013): allowlist contents. SEC-PG-03.</summary>
[Trait("Category", "SqlGate")]
public sealed class SqlFunctionAllowlistParityTests
{
    [Fact]
    public void NoDialect_AdvertisesLeftOrRight()
    {
        foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
        {
            Assert.DoesNotContain("left", SqlFunctionAllowlists.GetDefault(dialect));
            Assert.DoesNotContain("right", SqlFunctionAllowlists.GetDefault(dialect));
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public void DateTranslation_AddsOnlyDateFunctions()
    {
        // SEC-PG-03: nothing but the date family is added; the dialect default itself is unchanged.
        string[] expected = ["date_add", "date_part", "date_trunc", "day", "hour", "minute", "month", "now", "quarter", "second", "year"];
        foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
        {
            var basis = SqlFunctionAllowlists.GetDefault(dialect);
            var widened = SqlFunctionAllowlists.WithDateTranslation(basis);

            var added = widened.Except(basis, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            Assert.All(added, name => Assert.Contains(name, expected));
            Assert.Empty(basis.Except(widened, StringComparer.OrdinalIgnoreCase));
        }

        Assert.Equal(expected, SqlFunctionAllowlists.WithDateTranslation(new HashSet<string>()).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WithoutDateTranslation_RemovesThePartFunctionsWhereTheDialectHasNoNativeOne()
    {
        var names = SqlFunctionAllowlists.CompilerNames(TargetSqlDialect.Oracle);
        var without = SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.Oracle, names);

        Assert.Contains("year", names);
        Assert.DoesNotContain("year", without);
        Assert.DoesNotContain("date_part", without);
        Assert.Contains("year", SqlFunctionAllowlists.WithoutDateTranslation(TargetSqlDialect.SqlServer, SqlFunctionAllowlists.CompilerNames(TargetSqlDialect.SqlServer)));
    }
}
