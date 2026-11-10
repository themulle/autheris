namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class SqlFunctionPolicyHardeningTests
{
    private readonly FastSqlEngine _engine = new();

    private void Build(string sql, AstBuilderOptions? options = null)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        new SqlAstBuilder(options).BuildStatement(tree);
    }

    [Fact]
    public void Performance_FrozenSetCaseInsensitiveMatching_WorksWithoutFallback()
    {
        var set = new[] { "count", "sum", "avg", "lpad", "rpad" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Case-insensitive matches succeed
        Assert.True(SqlFunctionPolicy.ContainsIgnoreCase(set, "COUNT"));
        Assert.True(SqlFunctionPolicy.ContainsIgnoreCase(set, "cOuNt"));
        Assert.True(SqlFunctionPolicy.ContainsIgnoreCase(set, "sum"));
        Assert.True(SqlFunctionPolicy.ContainsIgnoreCase(set, "LPAD"));

        // Misses return false immediately
        Assert.False(SqlFunctionPolicy.ContainsIgnoreCase(set, "unknown_function"));
        Assert.False(SqlFunctionPolicy.ContainsIgnoreCase(set, "read_parquet"));
    }

    [Theory]
    [InlineData("SELECT lpad('x', 2000000000, 'y') FROM orders")]
    [InlineData("SELECT rpad('x', 1000000000, 'y') FROM orders")]
    [InlineData("SELECT replicate('x', 2000000000) FROM orders")]
    [InlineData("SELECT lpad('x', 4001, 'y') FROM orders")]
    [InlineData("SELECT rpad('x', 5000, 'y') FROM orders")]
    [InlineData("SELECT replicate('x', 8193) FROM orders")]
    [InlineData("SELECT lpad(status, length(status) * 100, 'x') FROM orders")]
    public void ResourceExhaustion_UnboundedOrOversizedRepeatLength_IsRejected(string sql)
    {
        Assert.Throws<AstBuildException>(() => Build(sql));
    }

    [Theory]
    [InlineData("SELECT lpad('x', 10, 'y') FROM orders")]
    [InlineData("SELECT rpad('x', 4000, 'y') FROM orders")]
    [InlineData("SELECT replicate('x', 10) FROM orders")]
    public void ResourceExhaustion_BoundedRepeatLength_IsAccepted(string sql)
    {
        Build(sql);
    }

    [Fact]
    public void FailClosedPolicy_DefaultDenylist_RejectsDangerousFunctions()
    {
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("read_parquet"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("read_csv"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("getenv"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("duckdb_settings"));
        Assert.True(SqlFunctionPolicy.IsDeniedByDefault("duckdb_secrets"));
        Assert.False(SqlFunctionPolicy.IsDeniedByDefault("count"));
        Assert.False(SqlFunctionPolicy.IsDeniedByDefault("lower"));
    }

    [Fact]
    public void FailClosedPolicy_AllowlistInEffect_RejectsUnknownFunctions()
    {
        var duckDbAllowed = SqlFunctionAllowlists.DuckDb;

        // Known safe functions allowed
        Assert.True(SqlFunctionPolicy.IsFunctionAllowed("count", duckDbAllowed, additionalDeniedFunctions: null));
        Assert.True(SqlFunctionPolicy.IsFunctionAllowed("lower", duckDbAllowed, additionalDeniedFunctions: null));

        // DuckDB file / environment functions rejected by allowlist
        Assert.False(SqlFunctionPolicy.IsFunctionAllowed("read_parquet", duckDbAllowed, additionalDeniedFunctions: null));
        Assert.False(SqlFunctionPolicy.IsFunctionAllowed("read_csv", duckDbAllowed, additionalDeniedFunctions: null));
        Assert.False(SqlFunctionPolicy.IsFunctionAllowed("getenv", duckDbAllowed, additionalDeniedFunctions: null));
        Assert.False(SqlFunctionPolicy.IsFunctionAllowed("duckdb_settings", duckDbAllowed, additionalDeniedFunctions: null));
    }

    [Theory]
    [InlineData("SELECT read_parquet('secret.parquet') FROM orders")]
    [InlineData("SELECT getenv('API_KEY') FROM orders")]
    [InlineData("SELECT duckdb_settings() FROM orders")]
    public void FailClosedPolicy_DefaultAstBuilder_RejectsDenylistedFunctions(string sql)
    {
        // Default AstBuilderOptions rejects denylisted functions
        Assert.Throws<SecurityException>(() => Build(sql));
    }

    [Fact]
    public void FailClosedPolicy_ExplicitAllowlist_RejectsUnknownFunctions()
    {
        var options = new AstBuilderOptions { AllowedFunctions = SqlFunctionAllowlists.DuckDb };
        Assert.Throws<SecurityException>(() => Build("SELECT custom_udf(id) FROM orders", options));
    }
}
