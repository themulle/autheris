namespace TrinoSqlEngine.Tests.TimeZones;

using TrinoSqlEngine.Ast.Capabilities;
using Xunit;

public sealed class TimeZoneAllowlistTests
{
    private static readonly TargetSqlDialect[] All =
        [TargetSqlDialect.Ansi, TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Sqlite, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks];

    private static readonly string[] Unlisted = ["timezone", "at_timezone", "with_timezone", "switchoffset", "todatetimeoffset", "timezone_hour", "timezone_minute"];

    [Fact]
    public void T7_ConvertTimezoneIsInEveryDialectAllowlist()
    {
        foreach (var dialect in All)
        {
            Assert.Contains("convert_timezone", SqlFunctionAllowlists.Build(dialect, null));
            Assert.Contains("convert_timezone", SqlFunctionAllowlists.GetDefault(dialect));
        }

        foreach (var dialect in new[] { TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Sqlite, TargetSqlDialect.DuckDb, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks })
        {
            Assert.Contains("convert_timezone", SqlFunctionAllowlists.CompilerNames(dialect));
        }

        Assert.False(SqlFunctionPolicy.IsDeniedByDefault("convert_timezone"));
    }

    [Fact]
    public void T7_TheRelatedZoneFunctionsAreInNoAllowlist()
    {
        foreach (var dialect in All)
        {
            foreach (var name in Unlisted)
            {
                Assert.DoesNotContain(name, SqlFunctionAllowlists.Build(dialect, null));
                Assert.DoesNotContain(name, SqlFunctionAllowlists.CompilerNames(dialect));
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_130_02_BC_130_2_PolicyAllowlistsDoNotAdmitConvertTimezone()
    {
        foreach (var dialect in All)
        {
            var policy = SqlFunctionAllowlists.BuildForPolicy(dialect, null);
            Assert.DoesNotContain("convert_timezone", policy);
            Assert.Contains("lower", policy);

            // An additional entry never brings it back, in any spelling.
            Assert.DoesNotContain("convert_timezone", SqlFunctionAllowlists.BuildForPolicy(dialect, ["CONVERT_TIMEZONE", " convert_timezone "]));
        }
    }

    [Fact]
    public void BuildForPolicyKeepsTheRestOfTheDialectAllowlist()
    {
        foreach (var dialect in All)
        {
            var build = SqlFunctionAllowlists.Build(dialect, ["abs"]);
            var policy = SqlFunctionAllowlists.BuildForPolicy(dialect, ["abs"]);
            Assert.Equal(build.Where(n => n != "convert_timezone").Order(StringComparer.Ordinal), policy.Order(StringComparer.Ordinal));
        }
    }
}
