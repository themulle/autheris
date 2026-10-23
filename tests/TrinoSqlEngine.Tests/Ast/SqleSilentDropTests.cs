namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>SQLE-14: constructs the AST cannot represent are rejected instead of being rewritten silently.</summary>
[Trait("Category", "SqlGate")]
public sealed class SqleSilentDropTests
{
    private readonly FastSqlEngine _engine = new();

    private void Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        new SqlAstBuilder().BuildStatement(tree);
    }

    [Theory]
    [InlineData("SELECT lower(status).* FROM orders")]
    [InlineData("SELECT o.* AS (a, b, c) FROM orders o")]
    [InlineData("SELECT substr(length => 2, string => status, start => 1) FROM orders")]
    [InlineData("SELECT ROW(id AS a, status AS b) FROM orders")]
    public void SilentlyDroppedConstruct_IsRejected(string sql)
    {
        var ex = Assert.Throws<AstBuildException>(() => Build(sql));
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT o.* FROM orders o")]
    [InlineData("SELECT * FROM orders")]
    [InlineData("SELECT substr(status, 1, 2) FROM orders")]
    [InlineData("SELECT ROW(id, status) FROM orders")]
    public void PlainForms_AreAccepted(string sql) => Build(sql);
}
