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
public sealed class SqlServerStrposBinaryCollationTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");

    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(
            Orders,
            ImmutableArray.Create(
                new CatalogColumn("Id", "int"),
                new CatalogColumn("TenantId", "nvarchar(64)"),
                new CatalogColumn("Email", "nvarchar(200)"),
                new CatalogColumn("Region", "nvarchar(40)")),
            "TenantId",
            1)
    }, "dbo");

    private static CompiledSql CompileSqlServer(string sql) =>
        new FastSqlEngine().Compile(
            sql.AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.SqlServer,
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
    [InlineData("SELECT strpos(email, 'A') AS r FROM orders", "CHARINDEX((@p0) COLLATE Latin1_General_BIN2, ([email]) COLLATE Latin1_General_BIN2)")]
    [InlineData("SELECT strpos(email, region) AS r FROM orders", "CHARINDEX(([region]) COLLATE Latin1_General_BIN2, ([email]) COLLATE Latin1_General_BIN2)")]
    public void Strpos_SqlServer_EmitsLatin1GeneralBin2Collation(string sql, string expected)
    {
        var compiled = CompileSqlServer(sql);

        Assert.Contains(expected, compiled.Sql, StringComparison.Ordinal);
    }
}
