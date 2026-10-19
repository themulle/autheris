namespace Autheris.Tests.Unit.Telemetry;

using Autheris.Domain.Diagnostics;
using Shouldly;
using Xunit;

public sealed class TelemetryNamesTests
{
    [Theory]
    [InlineData("mssql-primary", "mssql-primary")]
    [InlineData("postgres_01", "postgres_01")]
    [InlineData("sales.warehouse", "sales.warehouse")]
    [InlineData("db-2", "db-2")]
    [InlineData("  trimmed-source  ", "trimmed-source")]
    public void DataSource_ValidNames_ReturnsNormalized(string input, string expected)
    {
        TelemetryNames.DataSource(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sales db")]
    [InlineData("crm;drop table users")]
    [InlineData("crm/db")]
    [InlineData("db$name")]
    [InlineData("db@host")]
    [InlineData("source:port")]
    public void DataSource_InvalidOrUnsafeCharacters_ReturnsInvalid(string? input)
    {
        TelemetryNames.DataSource(input).ShouldBe("invalid");
    }

    [Fact]
    public void DataSource_LengthBoundaries_Enforced()
    {
        var valid64 = new string('a', 64);
        var invalid65 = new string('a', 65);

        TelemetryNames.DataSource(valid64).ShouldBe(valid64);
        TelemetryNames.DataSource(invalid65).ShouldBe("invalid");
    }
}
