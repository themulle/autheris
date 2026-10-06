namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Security;
using System.Text;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

public sealed class AstDepthGuardTests
{
    private readonly FastSqlEngine _engine = new();

    [Fact]
    public void DepthGuard_ExcessiveAstDepth_ThrowsSecurityException()
    {
        // Build 70 levels of nested parentheses
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < 70; i++) sb.Append('(');
        sb.Append('1');
        for (int i = 0; i < 70; i++) sb.Append(')');

        var (tree, _) = _engine.Parse(sb.ToString().AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(new AstBuilderOptions { MaxAllowedAstDepth = 64 });

        var ex = Assert.Throws<SecurityException>(() => builder.BuildStatement(tree));
        Assert.Contains("maximum allowable AST nesting depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DepthGuard_ModerateAstDepth_Succeeds()
    {
        // 20 levels of nesting is well within 64
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < 20; i++) sb.Append('(');
        sb.Append('1');
        for (int i = 0; i < 20; i++) sb.Append(')');

        var (tree, _) = _engine.Parse(sb.ToString().AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(new AstBuilderOptions { MaxAllowedAstDepth = 64 });

        var stmt = builder.BuildStatement(tree);
        Assert.NotNull(stmt);
    }
}
