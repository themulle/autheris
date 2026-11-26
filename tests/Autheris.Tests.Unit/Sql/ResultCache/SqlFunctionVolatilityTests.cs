namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

public sealed class SqlFunctionVolatilityTests
{
    public static TheoryData<TargetSqlDialect> Dialects() => new(
        TargetSqlDialect.Ansi, TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle, TargetSqlDialect.Databricks);

    [Theory]
    [InlineData("lower")]
    [InlineData("COALESCE")]
    [InlineData("sum")]
    [InlineData("substring")]
    public void Deterministic_known_functions_are_cache_safe(string name)
    {
        SqlFunctionVolatility.IsCacheSafe(name, TargetSqlDialect.PostgreSql).ShouldBeTrue();
    }

    [Theory]
    [InlineData("now")]
    [InlineData("NOW")]
    [InlineData("current_timestamp")]
    [InlineData("current_date")]
    [InlineData("getdate")]
    [InlineData("sysdate")]
    [InlineData("random")]
    [InlineData("newid")]
    [InlineData("current_user")]
    [InlineData("session_user")]
    [InlineData("current_role")]
    [InlineData("txid_current")]
    [InlineData("clock_timestamp")]
    [InlineData("timeofday")]
    public void Volatile_functions_are_not_cache_safe_for_every_dialect(string name)
    {
        foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
        {
            SqlFunctionVolatility.IsCacheSafe(name, dialect).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_name_is_not_cache_safe(string? name)
    {
        SqlFunctionVolatility.IsCacheSafe(name, TargetSqlDialect.Ansi).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Unknown_function_is_volatile_SECRC10()
    {
        SqlFunctionVolatility.IsCacheSafe("my_custom_udf", TargetSqlDialect.PostgreSql).ShouldBeFalse();
        SqlFunctionVolatility.IsCacheSafe("pg_catalog.lower", TargetSqlDialect.PostgreSql).ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Dialect_specific_function_is_unknown_for_other_dialects_SECRC10()
    {
        SqlFunctionVolatility.IsCacheSafe("nvl2", TargetSqlDialect.Oracle).ShouldBeTrue();
        SqlFunctionVolatility.IsCacheSafe("nvl2", TargetSqlDialect.SqlServer).ShouldBeFalse();
    }

    [Theory]
    [InlineData("SELECT NOW() FROM t")]
    [InlineData("SELECT a FROM t WHERE ts > current_timestamp")]
    [InlineData("select a from t where d = GETDATE()")]
    [InlineData("SELECT a FROM t WHERE u = SESSION_USER")]
    [InlineData("SELECT a FROM t ORDER BY random()")]
    [InlineData("SELECT x FROM t WHERE ts>now()")]
    public void Volatile_token_in_compiled_sql_is_found(string sql)
    {
        SqlFunctionVolatility.ContainsVolatileToken(sql).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SELECT a, b FROM t WHERE c = @p0")]
    [InlineData("SELECT known_value, renowned, snow_depth, random_seed_x FROM t")]
    [InlineData("SELECT a FROM t")]
    [InlineData("")]
    [InlineData(null)]
    public void Whole_word_scan_ignores_partial_matches(string? sql)
    {
        SqlFunctionVolatility.ContainsVolatileToken(sql).ShouldBeFalse();
    }
}
