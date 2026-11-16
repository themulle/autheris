namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class TargetSqlDialectTests
{
    [Fact]
    public void No_snowflake_value()
    {
        var names = Enum.GetNames<TargetSqlDialect>();
        Assert.DoesNotContain("Snowflake", names);

        var expectedSet = new HashSet<string>
        {
            nameof(TargetSqlDialect.Ansi),
            nameof(TargetSqlDialect.PostgreSql),
            nameof(TargetSqlDialect.SqlServer),
            nameof(TargetSqlDialect.DuckDb),
            nameof(TargetSqlDialect.Oracle),
            nameof(TargetSqlDialect.Databricks)
        };

        Assert.Equal(expectedSet, new HashSet<string>(names));
        Assert.Equal(6, names.Length);
    }

    [Fact]
    public void Only_supported_values_exist()
    {
        var actual = Enum.GetValues<TargetSqlDialect>().ToHashSet();
        var expected = new HashSet<TargetSqlDialect>
        {
            TargetSqlDialect.Ansi,
            TargetSqlDialect.PostgreSql,
            TargetSqlDialect.SqlServer,
            TargetSqlDialect.DuckDb,
            TargetSqlDialect.Oracle,
            TargetSqlDialect.Databricks
        };

        Assert.Equal(expected, actual);
    }
}
