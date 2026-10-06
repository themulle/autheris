namespace TrinoSqlEngine.Tests.Differential;

using System;
using TrinoSqlEngine;
using Xunit;

public sealed class LegacyVsAstDifferentialTests
{
    private readonly FastSqlEngine _engine = new();

    [Theory]
    [InlineData("SELECT id, name FROM users")]
    [InlineData("SELECT * FROM orders WHERE amount > 100")]
    [InlineData("SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id")]
    [InlineData("SELECT count(*) FROM items GROUP BY category")]
    public void Differential_LegacyVsAst_BothProduceGovernedSql(string query)
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string legacyResult = _engine.RewriteRls(query.AsMemory(), options);
        string astResult = _engine.GenerateGovernedSql(query.AsMemory(), options);

        Assert.NotEmpty(legacyResult);
        Assert.NotEmpty(astResult);

        // Both must inject tenant_id = 42 (AST dialect generator delimits identifiers)
        Assert.Contains("tenant_id = 42", legacyResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"tenant_id\" = 42", astResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShadowDualRun_ExecutesWithoutError()
    {
        var shadowEngine = new FastSqlEngine
        {
            SqlRewriterEngine = "ShadowDualRun"
        };

        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 99"),
            TargetDialect = TargetSqlDialect.SqlServer
        };

        string query = "SELECT id, amount FROM orders WHERE amount > 50";
        string result = shadowEngine.RewriteRls(query.AsMemory(), options);

        Assert.Contains("tenant_id = 99", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AstCompilerEngine_OptionRoute_WorksViaRewriteRls()
    {
        var astEngine = new FastSqlEngine
        {
            SqlRewriterEngine = "AstCompiler"
        };

        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 123"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string query = "SELECT * FROM products";
        string result = astEngine.RewriteRls(query.AsMemory(), options);

        Assert.Contains("\"tenant_id\" = 123", result, StringComparison.OrdinalIgnoreCase);
    }
}
