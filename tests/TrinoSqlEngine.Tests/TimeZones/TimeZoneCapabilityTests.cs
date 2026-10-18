namespace TrinoSqlEngine.Tests.TimeZones;

using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class TimeZoneCapabilityTests
{
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, true)]
    [InlineData(TargetSqlDialect.PostgreSql, true)]
    [InlineData(TargetSqlDialect.DuckDb, true)]
    [InlineData(TargetSqlDialect.Sqlite, false)]
    [InlineData(TargetSqlDialect.Oracle, false)]
    [InlineData(TargetSqlDialect.Databricks, false)]
    public void TheCapabilityFlagIsSetOnlyForTheDialectsWithARule(TargetSqlDialect dialect, bool expected) =>
        Assert.Equal(expected, DialectCapabilityTable.Default.Get(dialect).SupportsTimeZoneConversion);

    [Fact]
    public void TheCompileRequestFlagDefaultsToFalseAndIsPartOfTheCacheKey()
    {
        var request = TimeZoneTestSupport.Request(TargetSqlDialect.PostgreSql);
        Assert.False(request.TimeZoneConversionAvailable);
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);
        string off = CompileCacheKey.Material(request, "SELECT 1", caps, 1000);
        string on = CompileCacheKey.Material(request with { TimeZoneConversionAvailable = true }, "SELECT 1", caps, 1000);
        Assert.NotEqual(off, on);
    }
}
