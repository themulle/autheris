namespace TrinoSqlEngine.Tests;

using System;
using System.Threading;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>
/// SQL-7: the enforced row limit must not end up in a subquery of a DML statement (it would change which rows are
/// written). SQL-8: the alias of a rewritten table keeps the quoting of the written name; the legacy alias tests (string rewriter
/// output) are replaced by AstSecurityRegressionTests.SQL8_*. SQLite is covered here because the shared cases do not run it.
/// </summary>
public sealed class DmlLimitAndAliasSql78Tests
{
    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void AstCompiler_DmlSubquery_HasNoRowLimit(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);

        var compiled = env.Engine.Compile(
            "DELETE FROM orders WHERE id IN (SELECT id FROM orders o WHERE o.amount > 3)".AsMemory(),
            env.Request(enforcedMaxRows: 100), CancellationToken.None);

        Assert.DoesNotContain("LIMIT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH NEXT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TenantId", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "LIMIT 100")]
    [InlineData(TargetSqlDialect.SqlServer, "FETCH NEXT 100 ROWS ONLY")]
    public void AstCompiler_RootSelect_StillHasRowLimit(TargetSqlDialect dialect, string expected)
    {
        var env = new ProbeEnv(dialect);

        var compiled = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);

        Assert.Contains(expected, compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AstCompiler_Sqlite_RootSelect_HasABoundRowLimit()
    {
        var env = new ProbeEnv(TargetSqlDialect.Sqlite);

        var compiled = env.Engine.Compile("SELECT id FROM orders".AsMemory(), env.Request(enforcedMaxRows: 100), CancellationToken.None);

        Assert.Contains("LIMIT ?", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters, p => p.Value is not string && p.Value is not null && Convert.ToInt64(p.Value) == 100L);
    }
}
