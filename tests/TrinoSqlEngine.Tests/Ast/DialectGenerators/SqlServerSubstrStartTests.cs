namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using System.Collections.Immutable;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

[Trait("Category", "SqlGate")]
[RowPolicyGuardOff]
public sealed class SqlServerSubstrStartTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");

    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(
            Orders,
            ImmutableArray.Create(
                new CatalogColumn("Id", "int"),
                new CatalogColumn("TenantId", "nvarchar(64)"),
                new CatalogColumn("Email", "nvarchar(200)")),
            "TenantId",
            1)
    }, "dbo");

    private static CompiledSql Compile(string sql, TargetSqlDialect dialect) =>
        new FastSqlEngine().Compile(
            sql.AsMemory(),
            new CompileRequest
            {
                TargetDialect = dialect,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                Policy = new GovernancePolicy
                {
                    RowFilters = new DictPolicyProvider(),
                    Masks = new DictMaskProvider(),
                    Catalog = Catalog,
                    Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
                }
            },
            CancellationToken.None);

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, 0) AS r FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, 0, 3) AS r FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substring(email FROM 0 FOR 3) AS r FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, 0) AS r FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, 0, 3) AS r FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substring(email FROM 0 FOR 3) AS r FROM orders")]
    public void Substr_StartZero_EmitsEmptyString(TargetSqlDialect dialect, string sql)
    {
        var compiled = Compile(sql, dialect);

        Assert.Contains("''", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBSTR", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, 2) AS r FROM orders", "SUBSTRING([email], @p0, @p1)")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, 2, 3) AS r FROM orders", "SUBSTRING([email], @p0, @p1)")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, 2) AS r FROM orders", "SUBSTR(\"email\", $1)")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, 2, 3) AS r FROM orders", "SUBSTR(\"email\", $1, $2)")]
    public void Substr_PositiveLiteralStart_EmitsDirectSubstring(TargetSqlDialect dialect, string sql, string expected)
    {
        var compiled = Compile(sql, dialect);

        Assert.Contains(expected, compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE WHEN", compiled.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, -2) AS r FROM orders", "CASE WHEN LEN([email]) + @p0 + 1 >= 1 THEN SUBSTRING([email], LEN([email]) + @p0 + 1, @p1) ELSE '' END")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substr(email, -2, 1) AS r FROM orders", "CASE WHEN LEN([email]) + @p0 + 1 >= 1 THEN SUBSTRING([email], LEN([email]) + @p0 + 1, @p1) ELSE '' END")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT substring(email FROM -2 FOR 1) AS r FROM orders", "CASE WHEN LEN([email]) + @p0 + 1 >= 1 THEN SUBSTRING([email], LEN([email]) + @p0 + 1, @p1) ELSE '' END")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, -2) AS r FROM orders", "CASE WHEN CHAR_LENGTH(\"email\") + $1 + 1 >= 1 THEN SUBSTR(\"email\", CHAR_LENGTH(\"email\") + $1 + 1) ELSE '' END")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substr(email, -2, 1) AS r FROM orders", "CASE WHEN CHAR_LENGTH(\"email\") + $1 + 1 >= 1 THEN SUBSTR(\"email\", CHAR_LENGTH(\"email\") + $1 + 1, $2) ELSE '' END")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT substring(email FROM -2 FOR 1) AS r FROM orders", "CASE WHEN CHAR_LENGTH(\"email\") + $1 + 1 >= 1 THEN SUBSTRING(\"email\", CHAR_LENGTH(\"email\") + $1 + 1, $2) ELSE '' END")]
    public void Substr_NegativeLiteralStart_CountsFromEnd(TargetSqlDialect dialect, string sql, string expected)
    {
        var compiled = Compile(sql, dialect);

        Assert.Contains(expected, compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(
            compiled.Parameters,
            p => p.Origin == ParameterOrigin.QueryLiteral
                && p.Value is int or long
                && Convert.ToInt64(p.Value, System.Globalization.CultureInfo.InvariantCulture) == -2L);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    public void Substr_NegativeLiteralStart_OnCompoundOperand_IsRejected(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() =>
            Compile("SELECT substr(upper(email), -2) AS r FROM orders", dialect));

        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SUBSTRING([email], [id], @p0)")]
    [InlineData(TargetSqlDialect.PostgreSql, "SUBSTR(\"email\", \"id\")")]
    public void Substr_NonLiteralStart_UsesDirectFastPath(TargetSqlDialect dialect, string expected)
    {
        var compiled = Compile("SELECT substr(email, id) AS r FROM orders", dialect);

        Assert.Contains(expected, compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE WHEN", compiled.Sql, StringComparison.Ordinal);
    }
}
