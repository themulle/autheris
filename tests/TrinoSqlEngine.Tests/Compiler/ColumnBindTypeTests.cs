using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-09 / SEC-ADG-16 item 2: tenant and policy binds take their type from the catalog type of the compared column.</summary>
public class ColumnBindTypeTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");
    private static readonly TableIdentity Entitlements = new("dbo", "Entitlements");

    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private static InMemoryTableCatalog Catalog(string ordersTenantType, string entitlementsTenantType = "nvarchar(64)", string regionType = "varchar(20)") => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", ordersTenantType), new CatalogColumn("Region", regionType)), "TenantId", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", entitlementsTenantType), new CatalogColumn("OrderId", "int")), "TenantId", 1)
    }, "dbo");

    private CompiledSql Compile(string sql, InMemoryTableCatalog catalog, string tenant = "acme", TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        _engine.Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = _masks,
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
            }
        }, CancellationToken.None);

    private static PolicyPredicate Compare(string column, string value, BinaryOperator op = BinaryOperator.Equal) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(column, true) })), op,
            new PolicyParameterExpression("__pol_v", SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_v"] = new(value, SqlParameterType.String) });

    private static DbParameter0 Bind(CompiledSql compiled, int index)
    {
        using var command = new SqliteCommand();
        new SqlServerCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var p = command.Parameters[index];
        return new DbParameter0(p.DbType, p.Size, p.Value);
    }

    private sealed record DbParameter0(DbType DbType, int Size, object? Value);

    [Fact]
    public void BindType_FollowsCatalogColumnType_ForComparisons()
    {
        var varchar = Compile("SELECT id FROM orders", Catalog("varchar(64)"));
        var tenant = Assert.Single(varchar.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Equal("varchar(64)", tenant.ColumnType);
        var ansi = Bind(varchar, tenant.Ordinal);
        Assert.Equal(DbType.AnsiString, ansi.DbType);   // varchar column, varchar bind: the seek survives
        Assert.Equal(64, ansi.Size);

        var nvarchar = Compile("SELECT id FROM orders", Catalog("nvarchar(64)"));
        var unicode = Bind(nvarchar, Assert.Single(nvarchar.Parameters, p => p.Origin == ParameterOrigin.Tenant).Ordinal);
        Assert.Equal(DbType.String, unicode.DbType);
    }

    [Fact]
    public void AnsiBind_NeverTruncatesALongTenantValue()
    {
        string longTenant = new('a', 70);
        var compiled = Compile("SELECT id FROM orders", Catalog("varchar(64)"), tenant: longTenant);
        var bound = Bind(compiled, Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant).Ordinal);
        Assert.Equal(DbType.AnsiString, bound.DbType);
        Assert.True(bound.Size >= 70 || bound.Size == -1, "the parameter must not be smaller than the value (a provider truncates)");
        Assert.Equal(longTenant, bound.Value);
    }

    [Fact]
    public void PolicyParameter_ComparedWithAColumn_CarriesTheColumnType()
    {
        _rowFilters.Predicates[Orders] = Compare("Region", "EU");
        var compiled = Compile("SELECT id FROM orders", Catalog("nvarchar(64)"));
        var policy = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
        Assert.Equal("varchar(20)", policy.ColumnType);
        Assert.Equal(DbType.AnsiString, Bind(compiled, policy.Ordinal).DbType);
    }

    [Theory]
    [InlineData(BinaryOperator.NotEqual)]
    [InlineData(BinaryOperator.LessThan)]
    [InlineData(BinaryOperator.GreaterThanOrEqual)]
    public void PolicyParameter_InOtherComparisons_CarriesTheColumnType(BinaryOperator op)
    {
        _rowFilters.Predicates[Orders] = Compare("Region", "EU", op);
        var compiled = Compile("SELECT id FROM orders", Catalog("nvarchar(64)"));
        Assert.Equal("varchar(20)", Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy).ColumnType);
    }

    [Fact]
    public void PolicyParameter_InASubquery_KeepsTheValueType()
    {
        // The column inside the subquery belongs to another table; the target entry must not guess its type.
        var subquery = new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e", true), new SqlIdentifier("Id", true) })), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("Entitlements", true) }), new SqlIdentifier("e", true)),
                new BinaryExpression(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("e", true), new SqlIdentifier("OrderId", true) })),
                    BinaryOperator.Equal, new PolicyParameterExpression("__pol_v", SqlParameterType.String)),
                null, null), null, null));
        _rowFilters.Predicates[Orders] = PolicyPredicate.Create(subquery, new Dictionary<string, PolicyValue> { ["__pol_v"] = new("7", SqlParameterType.String) });
        var compiled = Compile("SELECT id FROM orders", Catalog("nvarchar(64)"));
        Assert.All(compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Policy), p => Assert.Null(p.ColumnType));
    }

    [Fact]
    public void TenantValue_IsBoundOncePerDistinctColumnType()
    {
        var compiled = Compile("SELECT o.id FROM orders o JOIN entitlements e ON e.orderid = o.id", Catalog("varchar(64)", "nvarchar(64)"));
        var tenants = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant).ToList();
        Assert.Equal(2, tenants.Count);
        Assert.Equal(new[] { "nvarchar(64)", "varchar(64)" }, tenants.Select(t => t.ColumnType).OrderBy(x => x, StringComparer.Ordinal));

        var same = Compile("SELECT o.id FROM orders o JOIN entitlements e ON e.orderid = o.id", Catalog("nvarchar(64)", "nvarchar(64)"));
        Assert.Single(same.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Fact]
    public void CacheHit_KeepsTheColumnTypeAndRebindsTheValue()
    {
        var first = Compile("SELECT id FROM orders", Catalog("varchar(64)"), tenant: "acme");
        var hit = Compile("SELECT id FROM orders", Catalog("varchar(64)"), tenant: "other");
        Assert.True(_engine.CompileCache.Stats.Hits >= 1);
        var tenant = Assert.Single(hit.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Equal("varchar(64)", tenant.ColumnType);
        Assert.Equal("other", tenant.Value);
        _ = first;
    }

    [Theory]
    [InlineData("VARCHAR2(64)", ColumnTypeClass.AnsiText)]
    [InlineData("varchar(max)", ColumnTypeClass.AnsiText)]
    [InlineData("nvarchar(64)", ColumnTypeClass.UnicodeText)]
    [InlineData("NVARCHAR2(10)", ColumnTypeClass.UnicodeText)]
    [InlineData("NUMBER(10,2)", ColumnTypeClass.Numeric)]
    [InlineData("decimal(18,2)", ColumnTypeClass.Numeric)]
    [InlineData("int", ColumnTypeClass.Numeric)]
    [InlineData("uniqueidentifier", ColumnTypeClass.Other)]
    [InlineData("", ColumnTypeClass.Unknown)]
    [InlineData("varchar(64); DROP TABLE x", ColumnTypeClass.Unknown)]
    public void Classify_CatalogTypes(string type, ColumnTypeClass expected) =>
        Assert.Equal(expected, ColumnBindAdapter.Classify(type));

    [Fact]
    public void Oracle_NumericColumn_BindsANumericLookingStringAsNumber_AvoidingOra01722()
    {
        var adapted = ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "42", "NUMBER(10)");
        Assert.Equal(SqlParameterType.Decimal, adapted.Type);
        Assert.Equal(42m, adapted.Value);

        // A value that is not a number stays text: Oracle then fails closed instead of converting the column.
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.String, "acme", "NUMBER(10)").Type);
    }

    [Fact]
    public void Oracle_TextColumn_BindsANumberAsText_NeverConvertingTheColumn()
    {
        var adapted = ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Int32, 5, "VARCHAR2(20)");
        Assert.Equal(SqlParameterType.String, adapted.Type);
        Assert.Equal("5", adapted.Value);
        Assert.Equal(SqlParameterType.Int32, ColumnBindAdapter.Adapt(ColumnBindStyle.Oracle, SqlParameterType.Int32, 5, "NUMBER(10)").Type);
    }

    [Fact]
    public void OtherDialects_KeepTheValueType()
    {
        Assert.Equal(SqlParameterType.String, ColumnBindAdapter.Adapt(ColumnBindStyle.None, SqlParameterType.String, "42", "NUMBER(10)").Type);
        var compiled = Compile("SELECT id FROM orders", Catalog("varchar(64)"), dialect: TargetSqlDialect.PostgreSql);
        using var command = new SqliteCommand();
        new PostgreSqlCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        Assert.All(command.Parameters.Cast<SqliteParameter>(), p => Assert.NotEqual(DbType.AnsiString, p.DbType));
    }
}
