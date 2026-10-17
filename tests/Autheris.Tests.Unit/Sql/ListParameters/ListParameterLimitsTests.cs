using Autheris.Application.Sql.ListParameters;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using Xunit;

namespace Autheris.Tests.Unit.Sql.ListParameters;

public class ListParameterLimitsTests
{
    private static int Effective(int configured, TargetSqlDialect dialect) =>
        ListParameterLimits.EffectiveMax(configured, DialectCapabilityTable.Default.Get(dialect));

    [Fact]
    public void Limits_EffectiveMax_PerDialect()
    {
        Effective(1000, TargetSqlDialect.SqlServer).ShouldBe(1000);      // cap 2,000
        Effective(5000, TargetSqlDialect.SqlServer).ShouldBe(2000);      // clamps to 2,100 - 100
        Effective(1000, TargetSqlDialect.Oracle).ShouldBe(1000);
        Effective(5000, TargetSqlDialect.Oracle).ShouldBe(1000);         // MaxInListItems (ORA-01795)
        Effective(5000, TargetSqlDialect.Databricks).ShouldBe(900);      // 1,000 - 100
        Effective(1000, TargetSqlDialect.PostgreSql).ShouldBe(1000);
        Effective(10_000, TargetSqlDialect.PostgreSql).ShouldBe(10_000);
        Effective(1000, TargetSqlDialect.Sqlite).ShouldBe(1000);
        Effective(1000, TargetSqlDialect.DuckDb).ShouldBe(1000);
    }

    [Fact]
    public void Limits_From_ReadsOptions_AndClampsToTheDocumentedRanges()
    {
        var defaults = ListParameterLimits.From(new WebSqlOptions());
        defaults.ConfiguredMaxValues.ShouldBe(1000);
        defaults.MaxListParameters.ShouldBe(8);

        var custom = ListParameterLimits.From(new WebSqlOptions { MaxListParameterValues = 50, MaxListParameters = 2 });
        custom.ConfiguredMaxValues.ShouldBe(50);
        custom.MaxListParameters.ShouldBe(2);

        var clamped = ListParameterLimits.From(new WebSqlOptions { MaxListParameterValues = 1_000_000, MaxListParameters = 1000 });
        clamped.ConfiguredMaxValues.ShouldBe(10_000);
        clamped.MaxListParameters.ShouldBe(64);

        var low = ListParameterLimits.From(new WebSqlOptions { MaxListParameterValues = 0, MaxListParameters = -3 });
        low.ConfiguredMaxValues.ShouldBe(1);
        low.MaxListParameters.ShouldBe(1);
    }

    [Fact]
    public void Limits_RawCeiling_IsTenTimesTheMaximumUpToOneHundredThousand()
    {
        new ListParameterLimits(1, 8).RawCeiling.ShouldBe(10);
        new ListParameterLimits(1000, 8).RawCeiling.ShouldBe(10_000);
        new ListParameterLimits(10_000, 8).RawCeiling.ShouldBe(100_000);
        new ListParameterLimits(50_000, 8).RawCeiling.ShouldBe(100_000);
    }
}

public class ListParameterOptionsValidationTests
{
    private static bool Valid(WebSqlOptions options) =>
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new System.ComponentModel.DataAnnotations.ValidationContext(options), [], validateAllProperties: true);

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1000, 8, true)]
    [InlineData(10_000, 64, true)]
    [InlineData(0, 8, false)]
    [InlineData(10_001, 8, false)]
    [InlineData(1000, 0, false)]
    [InlineData(1000, 65, false)]
    public void WebSqlListOptions_AreRangeChecked(int values, int lists, bool expected)
    {
        Valid(new WebSqlOptions { MaxListParameterValues = values, MaxListParameters = lists }).ShouldBe(expected);
    }
}
