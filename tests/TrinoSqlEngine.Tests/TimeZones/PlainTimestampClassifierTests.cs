namespace TrinoSqlEngine.Tests.TimeZones;

using TrinoSqlEngine.Ast.Security;
using Xunit;

public sealed class PlainTimestampClassifierTests
{
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "datetime", true)]
    [InlineData(TargetSqlDialect.SqlServer, "datetime2", true)]
    [InlineData(TargetSqlDialect.SqlServer, "datetime2(3)", true)]
    [InlineData(TargetSqlDialect.SqlServer, "DATETIME2 (7)", true)]
    [InlineData(TargetSqlDialect.SqlServer, "smalldatetime", true)]
    [InlineData(TargetSqlDialect.SqlServer, "datetimeoffset", false)]
    [InlineData(TargetSqlDialect.SqlServer, "datetimeoffset(7)", false)]
    [InlineData(TargetSqlDialect.SqlServer, "date", false)]
    [InlineData(TargetSqlDialect.SqlServer, "time", false)]
    [InlineData(TargetSqlDialect.SqlServer, "timestamp", false)]        // rowversion in T-SQL, never a time
    [InlineData(TargetSqlDialect.SqlServer, "TIMESTAMP", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp(3)", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp without time zone", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp(6) without time zone", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "TIMESTAMP WITHOUT TIME ZONE", true)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamptz", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp with time zone", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "timestamp(3) with time zone", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "date", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "time", false)]
    [InlineData(TargetSqlDialect.PostgreSql, "datetime2", false)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp", true)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp_s", true)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp_ms", true)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp_ns", true)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp without time zone", true)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamptz", false)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp with time zone", false)]
    [InlineData(TargetSqlDialect.DuckDb, "timestamp_tz", false)]
    [InlineData(TargetSqlDialect.DuckDb, "date", false)]
    public void T6_ClassificationTable(TargetSqlDialect dialect, string catalogType, bool expected) =>
        Assert.Equal(expected, PlainTimestampClassifier.IsPlain(dialect, catalogType));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("timestamp;DROP TABLE x")]
    [InlineData("timestamp(")]
    [InlineData("timestamp(x)")]
    public void T6_NullAndGarbageAreNeverPlain(string? catalogType)
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb })
        {
            Assert.False(PlainTimestampClassifier.IsPlain(dialect, catalogType));
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.Ansi)]
    public void T6_DialectsWithoutTheFunctionNeverClassifyAsPlain(TargetSqlDialect dialect) =>
        Assert.False(PlainTimestampClassifier.IsPlain(dialect, "timestamp"));

    [Theory]
    [InlineData("timestamp", true)]
    [InlineData("TIMESTAMP", true)]
    [InlineData("timestamp ( 3 )", true)]
    [InlineData("timestamp(3)", true)]
    [InlineData("timestamp without time zone", true)]
    [InlineData("timestamp with time zone", false)]
    [InlineData("timestamp ( 3 ) with time zone", false)]
    [InlineData("date", false)]
    [InlineData("varchar", false)]
    [InlineData("timestamptz", false)]
    public void T13_CastTargetsMustBePlainTimestamps(string target, bool expected) =>
        Assert.Equal(expected, PlainTimestampClassifier.IsPlainCastTarget(target));
}
