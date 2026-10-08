namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

public sealed class AstSecurityVisitorMaskingTests
{
    private readonly FastSqlEngine _engine = new();

    private string SecureAndGenerate(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options));
        var ast = builder.BuildStatement(tree);

        var visitor = new AstSecurityVisitor(options, _engine);
        var secured = (SqlStatement)visitor.Visit(ast);

        var generator = SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect);
        return generator.GenerateSql(secured);
    }

    [Fact]
    public void Masking_WildcardExpansion_AppliesMaskExpressionWithDelimitedQuotes()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("1=1"),
            TargetDialect = TargetSqlDialect.PostgreSql,
            TableColumnsProvider = table => new[] { "id", "email", "ssn" },
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                hasMaskPredicate: (t, c) => c.Equals("ssn", StringComparison.OrdinalIgnoreCase),
                maskExpressionProvider: (t, c) => "'***'")
        };

        string sql = "SELECT * FROM users";
        string result = SecureAndGenerate(sql, options);

        // SEC M-24: ssn should be masked with '***' AS "ssn"
        Assert.Contains("'***' AS \"ssn\"", result, StringComparison.Ordinal);
        Assert.Contains("\"id\"", result, StringComparison.Ordinal);
        Assert.Contains("\"email\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Masking_WithoutColumnProvider_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("1=1"),
            TargetDialect = TargetSqlDialect.PostgreSql,
            TableColumnsProvider = null, // No column provider
            TablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" },
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                hasMaskPredicate: (t, c) => true,
                maskExpressionProvider: (t, c) => "NULL")
        };

        string sql = "SELECT * FROM users";
        Assert.Throws<SecurityException>(() => SecureAndGenerate(sql, options));
    }
}
