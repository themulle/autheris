namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>SQLE-13: padding and replication functions take only a small literal length.</summary>
[Trait("Category", "SqlGate")]
public sealed class SqleRepeatLengthTests
{
    private readonly FastSqlEngine _engine = new();

    private void Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        new SqlAstBuilder().BuildStatement(tree);
    }

    [Theory]
    [InlineData("SELECT rpad(status, 1000000000, 'x') FROM orders")]
    [InlineData("SELECT lpad(status, 4001, 'x') FROM orders")]
    [InlineData("SELECT rpad(status, length(status) * 1000000, 'x') FROM orders")]
    [InlineData("SELECT replicate(status, 2000000000) FROM orders")]
    public void UnboundedLength_IsRejected(string sql) => Assert.Throws<AstBuildException>(() => Build(sql));

    [Theory]
    [InlineData("SELECT rpad(status, 10, 'x') FROM orders")]
    [InlineData("SELECT lpad(status, 4000, 'x') FROM orders")]
    public void SmallLiteralLength_IsAccepted(string sql) => Build(sql);
}
