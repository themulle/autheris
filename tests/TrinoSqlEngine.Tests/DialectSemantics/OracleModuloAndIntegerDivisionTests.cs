using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

namespace TrinoSqlEngine.Tests.DialectSemantics;

[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class OracleModuloAndIntegerDivisionTests
{
    private static readonly TableIdentity Items = new("dbo", "Items");

    private readonly FastSqlEngine _engine = new();

    private static InMemoryTableCatalog CatalogWithTypes(
        string aType = "INTEGER",
        string bType = "BIGINT",
        string decType = "DECIMAL(10, 2)",
        string dblType = "DOUBLE",
        string numZeroType = "NUMBER(10, 0)",
        string bareNumType = "NUMBER") =>
        new(new[]
        {
            new TableCatalogEntry(Items, ImmutableArray.Create(
                new CatalogColumn("TenantId", "varchar(64)"),
                new CatalogColumn("a", aType),
                new CatalogColumn("b", bType),
                new CatalogColumn("dec_col", decType),
                new CatalogColumn("dbl_col", dblType),
                new CatalogColumn("num0", numZeroType),
                new CatalogColumn("num_bare", bareNumType),
                new CatalogColumn("unk_col", "unknown_custom")),
                "TenantId", 1)
        }, "dbo");

    private CompiledSql CompileOracle(string sql, InMemoryTableCatalog? catalog = null)
    {
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Oracle,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider { NoPolicy = { Items } },
                Masks = new DictMaskProvider(),
                Catalog = catalog ?? CatalogWithTypes(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
        return _engine.Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    [Fact]
    public void Oracle_Modulo_Emits_MOD_Function()
    {
        var compiled = CompileOracle("SELECT a % 3 AS rem FROM items");
        Assert.Contains("MOD(\"A\", :p1)", compiled.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("%", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_IntegerLiteralDivision_Emits_TRUNC()
    {
        var compiled = CompileOracle("SELECT 7 / 2 AS q, (-7) / 3 AS neg_lit, (-a) / 5 AS neg_col FROM items");
        Assert.Contains("TRUNC(:p1 / :p2)", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("TRUNC(:p3 / :p4)", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("-(\"A\")", compiled.Sql, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(compiled.Sql, "TRUNC("));
    }

    [Theory]
    [InlineData("a / b")]
    [InlineData("a / num0")]
    [InlineData("CAST(dec_col AS INTEGER) / 2")]
    [InlineData("CAST(dec_col AS DECIMAL(10, 0)) / CAST(dbl_col AS BIGINT)")]
    [InlineData("(a + b) / 2")]
    public void Oracle_IntegerColumnDivision_Emits_TRUNC(string expr)
    {
        var compiled = CompileOracle($"SELECT {expr} AS q FROM items");
        Assert.Contains("TRUNC(", compiled.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dec_col / 2")]
    [InlineData("dbl_col / 2")]
    [InlineData("num_bare / 2")]
    [InlineData("unk_col / 2")]
    [InlineData("7.5 / 2")]
    [InlineData("7 / 2.0")]
    public void Oracle_DecimalOrFloatDivision_Emits_StandardSlash(string expr)
    {
        var compiled = CompileOracle($"SELECT {expr} AS q FROM items");
        Assert.DoesNotContain("TRUNC(", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(" / ", compiled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Oracle_NestedModuloAndDivision_EmitsEachOperandOnce()
    {
        var compiled = CompileOracle("SELECT ((a + b) % num0) + (a / 2) AS res FROM items");
        Assert.Contains("MOD(\"A\" + \"B\", \"NUM0\")", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("TRUNC(\"A\" / :p1)", compiled.Sql, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(compiled.Sql, "\"A\""));
        Assert.Equal(1, CountOccurrences(compiled.Sql, "\"B\""));
        Assert.Equal(1, CountOccurrences(compiled.Sql, "\"NUM0\""));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }
}
