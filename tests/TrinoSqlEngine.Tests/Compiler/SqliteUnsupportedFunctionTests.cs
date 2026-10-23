using System.Security;
using TrinoSqlEngine;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// The SQLite allowlist advertises only functions that SQLite can execute: a function without a SQLite equivalent is rejected
/// at compile time (fail closed) instead of reaching the provider as a 502.
/// </summary>
public class SqliteUnsupportedFunctionTests
{
    public static TheoryData<string, string> UnsupportedCalls => new()
    {
        { "stddev", "SELECT stddev(Amount) AS v FROM Orders" },
        { "stddev_pop", "SELECT stddev_pop(Amount) AS v FROM Orders" },
        { "stddev_samp", "SELECT stddev_samp(Amount) AS v FROM Orders" },
        { "variance", "SELECT variance(Amount) AS v FROM Orders" },
        { "var_pop", "SELECT var_pop(Amount) AS v FROM Orders" },
        { "var_samp", "SELECT var_samp(Amount) AS v FROM Orders" },
        { "greatest", "SELECT greatest(Id, 3) AS v FROM Orders" },
        { "least", "SELECT least(Id, 3) AS v FROM Orders" },
        { "lpad", "SELECT lpad(Region, 8, '*') AS v FROM Orders" },
        { "rpad", "SELECT rpad(Region, 8, '*') AS v FROM Orders" },
        { "reverse", "SELECT reverse(Region) AS v FROM Orders" },
        { "char_length", "SELECT char_length(Region) AS v FROM Orders" },
        { "character_length", "SELECT character_length(Region) AS v FROM Orders" },
    };

    [Theory]
    [MemberData(nameof(UnsupportedCalls))]
    public void Sqlite_FunctionWithoutSqliteEquivalent_IsNotAdvertised(string name, string sql)
    {
        _ = sql;
        Assert.DoesNotContain(name, SqlFunctionAllowlists.Sqlite);
        Assert.DoesNotContain(name, SqlFunctionAllowlists.Build(TargetSqlDialect.Sqlite, null));
        Assert.DoesNotContain(name, SqlFunctionAllowlists.CompilerNames(TargetSqlDialect.Sqlite));
    }

    [Theory]
    [MemberData(nameof(UnsupportedCalls))]
    public void Sqlite_FunctionWithoutSqliteEquivalent_IsRejectedAtCompileTime(string name, string sql)
    {
        _ = name;
        Assert.Throws<SecurityException>(() => SqliteAstSupportTests.Compile(sql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void OtherDialects_KeepTheFunctions(TargetSqlDialect dialect)
    {
        var allowlist = SqlFunctionAllowlists.GetDefault(dialect);
        foreach (var name in new[] { "stddev", "var_samp", "greatest", "least", "lpad", "rpad", "reverse", "char_length", "character_length" })
        {
            Assert.Contains(name, allowlist);
        }
    }
}
