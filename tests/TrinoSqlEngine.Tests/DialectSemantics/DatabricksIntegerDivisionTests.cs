using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.DialectSemantics;

[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class DatabricksIntegerDivisionTests
{
    private static readonly TableIdentity Items = new("dbo", "Items");

    private static InMemoryTableCatalog CatalogWithColumnType(string colType) =>
        new(new[]
        {
            new TableCatalogEntry(Items, ImmutableArray.Create(
                new CatalogColumn("TenantId", "varchar(64)"),
                new CatalogColumn("a", colType),
                new CatalogColumn("b", "INT4"),
                new CatalogColumn("dec_col", "DECIMAL(10, 2)"),
                new CatalogColumn("dbl_col", "DOUBLE")),
                "TenantId", 1)
        }, "dbo");

    private static CompileRequest DatabricksRequest(InMemoryTableCatalog catalog, int expansionFactor = 20) => new()
    {
        TargetDialect = TargetSqlDialect.Databricks,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        MaxExpansionFactor = expansionFactor,
        Policy = new GovernancePolicy
        {
            RowFilters = new DictPolicyProvider(),
            Masks = new DictMaskProvider(),
            Catalog = catalog,
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    };

    [Theory]
    [InlineData("7 / 2", "DIV")]
    [InlineData("a / 2", "`a` DIV :p1")]
    [InlineData("(a + b) / 2", "(`a` + `b`) DIV :p1")]
    [InlineData("a / b + 1", "`a` DIV `b` + :p1")]
    [InlineData("12 / (4 / 2)", ":p1 DIV (:p2 DIV :p3)")]
    [InlineData("CAST(dec_col AS BIGINT) / 3", "DIV :p1")]
    public void Databricks_IntegerDivision_Emits_DIV(string expr, string expectedFragment)
    {
        var engine = new FastSqlEngine();
        var compiled = engine.Compile($"SELECT {expr} AS q FROM items".AsMemory(), DatabricksRequest(CatalogWithColumnType("INTEGER")), CancellationToken.None);
        Assert.Contains(expectedFragment, compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" / ", compiled.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dec_col / 2")]
    [InlineData("dbl_col / 2")]
    [InlineData("7.5 / 2")]
    [InlineData("7 / 2.0")]
    [InlineData("CAST(a AS DOUBLE) / 2")]
    public void Databricks_DecimalOrFloatDivision_Emits_Slash(string expr)
    {
        var engine = new FastSqlEngine();
        var compiled = engine.Compile($"SELECT {expr} AS q FROM items".AsMemory(), DatabricksRequest(CatalogWithColumnType("INTEGER")), CancellationToken.None);
        Assert.DoesNotContain(" DIV ", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(" / ", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Databricks_CatalogColumnTypeChange_IntToDouble_InvalidatesCacheAndSwitchesFromDivToSlash()
    {
        var engine = new FastSqlEngine();
        const string sql = "SELECT a / 2 AS q FROM items";

        var intCompiled = engine.Compile(sql.AsMemory(), DatabricksRequest(CatalogWithColumnType("INTEGER")), CancellationToken.None);
        Assert.Contains("`a` DIV :p1", intCompiled.Sql, StringComparison.Ordinal);

        var dblCompiled = engine.Compile(sql.AsMemory(), DatabricksRequest(CatalogWithColumnType("DOUBLE")), CancellationToken.None);
        Assert.Contains("`a` / :p1", dblCompiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" DIV ", dblCompiled.Sql, StringComparison.Ordinal);
        Assert.Equal(0, engine.CompileCache.Stats.Hits);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DeepNestedDivision_ExceedingFourTimesMaxQueryLength_ThrowsSqlLimitExceeded()
    {
        const int maxQueryLength = 160;
        var engine = new FastSqlEngine { MaxQueryLength = maxQueryLength };
        string expr = string.Join("+", Enumerable.Repeat("1/2", 35));
        string sql = $"SELECT {expr} FROM items";
        Assert.True(sql.Length <= maxQueryLength, $"Input length {sql.Length} exceeded test limit {maxQueryLength}");

        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Oracle,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            MaxExpansionFactor = 20,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = CatalogWithColumnType("INTEGER"),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };

        var ex = Assert.Throws<SqlLimitExceededException>(() => engine.Compile(sql.AsMemory(), request, CancellationToken.None));
        Assert.Equal(SqlLimitKind.EmittedSqlLength, ex.Kind);
        Assert.Equal(4L * maxQueryLength, ex.Maximum);
    }
}
