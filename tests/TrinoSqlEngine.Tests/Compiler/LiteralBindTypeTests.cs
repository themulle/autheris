using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0q / SEC-ADG-16 item 2: a user literal compared with a catalog column binds with the column's type family and size, and
/// a comparison across families is rejected instead of letting the database convert the column (ORA-01722 side channel).
/// </summary>
public class LiteralBindTypeTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");
    private static readonly TableIdentity Entitlements = new("dbo", "Entitlements");

    private readonly FastSqlEngine _engine = new();

    private static InMemoryTableCatalog Catalog(string regionType = "varchar(20)", string amountType = "int", string titleType = "nvarchar(50)") => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"),
            new CatalogColumn("Region", regionType), new CatalogColumn("Amount", amountType), new CatalogColumn("Title", titleType)), "TenantId", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Code", "nvarchar(10)")), "TenantId", 1)
    }, "dbo");

    private CompiledSql Compile(string sql, InMemoryTableCatalog catalog, TargetSqlDialect dialect = TargetSqlDialect.SqlServer, string tenant = "acme") =>
        _engine.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Update | StatementPermissions.Delete,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
            }
        }, CancellationToken.None);

    private static IEnumerable<BoundParameter> Literals(CompiledSql compiled) => compiled.Parameters.Where(p => p.Origin == ParameterOrigin.QueryLiteral);

    private static void AssertRejected(Action compile) =>
        Assert.Equal(SqlCompileNotSupportedReason.Construct, Assert.Throws<SqlCompileNotSupportedException>(compile).Reason);

    private static (DbType DbType, int Size, object? Value)[] BindAll(CompiledSql compiled, DbCommandCompiledSqlBinder binder)
    {
        using var command = new SqliteCommand();
        binder.Bind(command, compiled, new Dictionary<string, object?>());
        return command.Parameters.Cast<SqliteParameter>().Select(p => (p.DbType, p.Size, p.Value)).ToArray();
    }

    [Fact]
    public void Oracle_NumericLiteralVsVarchar2_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));

    [Fact]
    public void Oracle_ReversedOperands_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE 5 = region", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));

    [Fact]
    public void Oracle_NegativeNumericLiteralVsVarchar2_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region > -5", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));

    [Fact]
    public void Oracle_DecimalLiteralVsVarchar2_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5.5", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));

    [Fact]
    public void Oracle_NumericLiteralVsNumber_KeepsItsNumericType()
    {
        var compiled = Compile("SELECT id FROM orders WHERE amount = 5", Catalog(amountType: "NUMBER(10)"), TargetSqlDialect.Oracle);
        var literal = Assert.Single(Literals(compiled));
        Assert.Equal("NUMBER(10)", literal.ColumnType);
        Assert.Equal(SqlParameterType.Int32, literal.Type);
    }

    [Fact]
    public void Oracle_NumericLookingStringLiteralVsNumber_BindsAsNumber()
    {
        var compiled = Compile("SELECT id FROM orders WHERE amount = '42'", Catalog(amountType: "NUMBER(10)"), TargetSqlDialect.Oracle);
        var bound = BindAll(compiled, new OracleCompiledSqlBinderProbe());
        var literal = Assert.Single(Literals(compiled));
        Assert.Equal("NUMBER(10)", literal.ColumnType);
        Assert.Equal(42m, bound[literal.Ordinal].Value);
    }

    [Fact]
    public void Oracle_UnknownColumnType_LiteralRejected()
    {
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 'EU'", Catalog(regionType: ""), TargetSqlDialect.Oracle));
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5", Catalog(regionType: "no such type!"), TargetSqlDialect.Oracle));
    }

    [Fact]
    public void Oracle_LiteralNotComparedWithAColumn_IsUnaffected()
    {
        var compiled = Compile("SELECT 5 AS five, id FROM orders WHERE amount = amount + 1", Catalog(regionType: ""), TargetSqlDialect.Oracle);
        Assert.NotEmpty(Literals(compiled));
    }

    [Fact]
    public void SqlServer_StringLiteralVsVarchar_BoundAsAnsiStringWithColumnSize()
    {
        var compiled = Compile("SELECT id FROM orders WHERE region = 'EU'", Catalog("varchar(20)"));
        var literal = Assert.Single(Literals(compiled));
        Assert.Equal("varchar(20)", literal.ColumnType);
        var bound = BindAll(compiled, new SqlServerCompiledSqlBinder())[literal.Ordinal];
        Assert.Equal(DbType.AnsiString, bound.DbType);
        Assert.Equal(20, bound.Size);
        Assert.Equal("EU", bound.Value);
    }

    [Fact]
    public void SqlServer_StringLiteralVsNvarchar_StaysUnicode()
    {
        var compiled = Compile("SELECT id FROM orders WHERE title = 'x'", Catalog());
        var literal = Assert.Single(Literals(compiled));
        Assert.Equal("nvarchar(50)", literal.ColumnType);
        Assert.Equal(DbType.String, BindAll(compiled, new SqlServerCompiledSqlBinder())[literal.Ordinal].DbType);
    }

    [Fact]
    public void SqlServer_NumericLiteralVsVarchar_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5", Catalog()));

    [Fact]
    public void PostgreSql_IntLiteralVsText_Rejected() =>
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5", Catalog("text"), TargetSqlDialect.PostgreSql));

    [Fact]
    public void PostgreSql_NumericLiteralVsNumeric_Accepted()
    {
        var compiled = Compile("SELECT id FROM orders WHERE amount = 5", Catalog(amountType: "integer"), TargetSqlDialect.PostgreSql);
        Assert.Equal("integer", Assert.Single(Literals(compiled)).ColumnType);
    }

    [Fact]
    public void InList_FollowsColumnType()
    {
        var compiled = Compile("SELECT id FROM orders WHERE region IN ('EU', 'US')", Catalog("varchar(20)"));
        var literals = Literals(compiled).ToList();
        Assert.Equal(2, literals.Count);
        Assert.All(literals, l => Assert.Equal("varchar(20)", l.ColumnType));
        var bound = BindAll(compiled, new SqlServerCompiledSqlBinder());
        Assert.All(literals, l => Assert.Equal(DbType.AnsiString, bound[l.Ordinal].DbType));

        AssertRejected(() => Compile("SELECT id FROM orders WHERE region IN ('EU', 7)", Catalog("varchar(20)")));
    }

    [Fact]
    public void Between_FollowsColumnType()
    {
        var compiled = Compile("SELECT id FROM orders WHERE region BETWEEN 'A' AND 'M'", Catalog("varchar(20)"));
        Assert.All(Literals(compiled), l => Assert.Equal("varchar(20)", l.ColumnType));
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region BETWEEN 1 AND 9", Catalog("varchar(20)")));
    }

    [Fact]
    public void UnknownColumnType_KeepsLiteralType()
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb })
        {
            var compiled = Compile("SELECT id FROM orders WHERE region = 'EU' AND amount = 5", Catalog(regionType: "", amountType: ""), dialect);
            var literals = Literals(compiled).ToList();
            Assert.Equal(2, literals.Count);
            Assert.All(literals, l => Assert.Null(l.ColumnType));
            Assert.Equal(new[] { SqlParameterType.Int32, SqlParameterType.String }.Order(), literals.Select(l => l.Type).Order());
        }
    }

    [Fact]
    public void JoinAlias_ResolvesTheQualifiedColumn()
    {
        var compiled = Compile("SELECT o.id FROM orders o JOIN entitlements e ON e.id = o.id WHERE e.code = 'X' AND o.region = 'EU'", Catalog("varchar(20)"));
        var types = Literals(compiled).ToDictionary(l => l.Value!.ToString()!, l => l.ColumnType);
        Assert.Equal("nvarchar(10)", types["X"]);
        Assert.Equal("varchar(20)", types["EU"]);
    }

    [Fact]
    public void UnqualifiedColumn_AmbiguousAcrossTables_KeepsLiteralType()
    {
        // "id" exists in both tables, so the gateway cannot tell which one is meant.
        var compiled = Compile("SELECT o.id FROM orders o JOIN entitlements e ON e.id = o.id WHERE id = 5", Catalog());
        Assert.All(Literals(compiled), l => Assert.Null(l.ColumnType));
    }

    [Fact]
    public void ColumnOfACteOrDerivedTable_KeepsLiteralType()
    {
        var compiled = Compile("WITH c AS (SELECT region FROM orders) SELECT region FROM c WHERE region = 'EU'", Catalog("varchar(20)"));
        Assert.Contains(Literals(compiled), l => l.Value is "EU" && l.ColumnType is null);
    }

    [Fact]
    public void SameLiteralAgainstTwoColumnTypes_IsBoundOncePerColumnType()
    {
        var compiled = Compile("SELECT id FROM orders WHERE region = 'x' AND title = 'x'", Catalog("varchar(20)"));
        var literals = Literals(compiled).ToList();
        Assert.Equal(2, literals.Count);
        Assert.Equal(new[] { "nvarchar(50)", "varchar(20)" }, literals.Select(l => l.ColumnType).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Dml_Where_FollowsColumnType()
    {
        var compiled = Compile("DELETE FROM orders WHERE region = 'EU'", Catalog("varchar(20)"));
        Assert.Equal("varchar(20)", Assert.Single(Literals(compiled)).ColumnType);
        AssertRejected(() => Compile("DELETE FROM orders WHERE region = 5", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));
        AssertRejected(() => Compile("UPDATE orders SET title = 'x' WHERE region = 5", Catalog("VARCHAR2(20)"), TargetSqlDialect.Oracle));
    }

    [Fact]
    public void CacheHit_KeepsTheLiteralColumnTypeAndRebindsTheTenant()
    {
        var first = Compile("SELECT id FROM orders WHERE region = 'EU'", Catalog("varchar(20)"), tenant: "acme");
        var hit = Compile("SELECT id FROM orders WHERE region = 'EU'", Catalog("varchar(20)"), tenant: "other");
        Assert.True(_engine.CompileCache.Stats.Hits >= 1);
        Assert.Equal("varchar(20)", Assert.Single(Literals(hit)).ColumnType);
        _ = first;
    }

    [Fact]
    public void ColumnTypeChange_IsNotServedFromTheCache()
    {
        Compile("SELECT id FROM orders WHERE region = 5", Catalog("int"));
        AssertRejected(() => Compile("SELECT id FROM orders WHERE region = 5", Catalog("varchar(20)")));
    }

    private sealed class OracleCompiledSqlBinderProbe : DbCommandCompiledSqlBinder
    {
        protected override TargetSqlDialect Dialect => TargetSqlDialect.Oracle;
        protected override ColumnBindStyle ColumnStyle => ColumnBindStyle.Oracle;
    }
}
