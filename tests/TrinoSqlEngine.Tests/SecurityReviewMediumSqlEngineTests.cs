namespace TrinoSqlEngine.Tests;

using System;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

public class SecurityReviewMediumSqlEngineTests
{
    private static ProbeEnv Env(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => new(dialect);

    [Fact]
    public void SR_M_25_UserCteWithSameNameAsPolicySubqueryTable_EmitsCatalogQualifiedReference()
    {
        var env = Env(TargetSqlDialect.SqlServer);
        env.PolicySubqueryPolicy();

        // Under AST compilation (X1 cutover), the policy subquery table is canonicalized
        // to a schema-qualified name (e.g. [dbo].[Entitlements]), preventing user CTE hijacking.
        string sql = "WITH Entitlements AS (SELECT 1 AS Id, 1 AS OrderId) SELECT id FROM orders";
        var compiled = env.Compile(sql);

        Assert.Contains("[dbo].[Entitlements]", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenantid, ID) VALUES (1, 'acme', 2)")]
    [InlineData("INSERT INTO orders (id, \"tenantid\", tenantid) VALUES (1, 'acme', 'acme')")]
    [InlineData("INSERT INTO orders (id, tenantid, id) VALUES (1, 'acme', 2)")]
    public void SR_M_26_DuplicateInsertColumn_IsRejected(string sql)
    {
        var ex = Assert.ThrowsAny<SecurityException>(() => Env().Compile(sql));
        Assert.Contains("A column is listed more than once", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SR_M_26_DistinctInsertColumns_AreAccepted()
    {
        var compiled = Env().Compile("INSERT INTO orders (id, tenantid) VALUES (1, 'acme')");
        Assert.Contains("INSERT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
