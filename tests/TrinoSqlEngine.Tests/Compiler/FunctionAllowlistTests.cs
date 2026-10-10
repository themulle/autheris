using System.Security;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Capabilities;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>CR-ADG-02: no rule means reject. The compiler is fail-closed by allowlist, per dialect.</summary>
public class FunctionAllowlistTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    private CompiledSql Compile(string sql, TargetSqlDialect dialect, IReadOnlySet<string>? allowed = null) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        AllowedFunctions = allowed,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
        }
    }, CancellationToken.None);

    public static IEnumerable<object[]> UnmappedFunctions() => new[]
    {
        new object[] { TargetSqlDialect.Databricks, "reflect('java.lang.System', 'getProperty', 'java.version')" },
        new object[] { TargetSqlDialect.Databricks, "java_method('java.lang.System', 'getProperty', 'java.version')" },
        new object[] { TargetSqlDialect.Databricks, "secret('scope', 'key')" },
        new object[] { TargetSqlDialect.SqlServer, "IS_ROLEMEMBER('db_owner')" },
        new object[] { TargetSqlDialect.SqlServer, "DATABASE_PRINCIPAL_ID('dbo')" },
        new object[] { TargetSqlDialect.Oracle, "DBURITYPE('/PUBLIC/ORDERS')" },
        new object[] { TargetSqlDialect.Oracle, "XMLTYPE('<a/>')" },
        // Not on any denylist: rejected only because no rule maps them.
        new object[] { TargetSqlDialect.PostgreSql, "some_extension_function(email)" },
        new object[] { TargetSqlDialect.DuckDb, "some_extension_function(email)" },
        new object[] { TargetSqlDialect.SqlServer, "some_extension_function(email)" },
        new object[] { TargetSqlDialect.Oracle, "some_extension_function(email)" },
        new object[] { TargetSqlDialect.Databricks, "some_extension_function(email)" },
        // A function of another dialect is not mapped either.
        new object[] { TargetSqlDialect.PostgreSql, "getdate()" },
        new object[] { TargetSqlDialect.Oracle, "datalength(email)" },
    };

    [Theory]
    [MemberData(nameof(UnmappedFunctions))]
    public void UnmappedFunction_IsRejected_PerDialect(TargetSqlDialect dialect, string call)
    {
        Assert.ThrowsAny<SecurityException>(() => Compile($"SELECT {call} FROM orders", dialect));
    }

    [Theory]
    [InlineData("reflect('java.lang.System', 'getProperty', 'java.version')")]
    [InlineData("java_method('java.lang.System', 'getProperty', 'java.version')")]
    [InlineData("secret('scope', 'key')")]
    public void Databricks_Reflect_JavaMethod_Secret_Rejected(string call)
    {
        Assert.ThrowsAny<SecurityException>(() => Compile($"SELECT {call} FROM orders", TargetSqlDialect.Databricks));
        Assert.ThrowsAny<SecurityException>(() => Compile($"SELECT id FROM orders WHERE status = {call}", TargetSqlDialect.Databricks));
    }

    /// <summary>CR-ADG-26: a delimited function name would bypass the built-in on PostgreSQL and Oracle; fail closed.</summary>
    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"lower\"(email)")]
    [InlineData(TargetSqlDialect.PostgreSql, "\"LOWER\"(email)")]
    [InlineData(TargetSqlDialect.PostgreSql, "\"pg_catalog\".lower(email)")]
    [InlineData(TargetSqlDialect.Oracle, "\"lower\"(email)")]
    [InlineData(TargetSqlDialect.Oracle, "\"UPPER\"(email)")]
    [InlineData(TargetSqlDialect.SqlServer, "\"lower\"(email)")]
    [InlineData(TargetSqlDialect.DuckDb, "\"lower\"(email)")]
    [InlineData(TargetSqlDialect.Databricks, "\"lower\"(email)")]
    public void DelimitedFunctionName_IsRejected(TargetSqlDialect dialect, string call)
    {
        Assert.ThrowsAny<SecurityException>(() => Compile($"SELECT {call} FROM orders", dialect));
        Assert.ThrowsAny<SecurityException>(() => Compile($"SELECT id FROM orders WHERE {call} = 'x'", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void MappedFunctions_StillCompile_WhenAllowedFunctionsIsNull(TargetSqlDialect dialect)
    {
        var c = Compile("SELECT upper(email), coalesce(status, 'x'), count(*) FROM orders GROUP BY email, status", dialect);
        Assert.NotEmpty(c.Sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    public void NullAllowedFunctions_MeansTheDialectMap_NeverEverythingNotDenied(TargetSqlDialect dialect)
    {
        var map = DialectCapabilityTable.Default.Get(dialect).Functions;
        Assert.NotEmpty(map.Rules);
        Assert.Contains("upper", map.Names);
        Assert.DoesNotContain("reflect", map.Names);
        Assert.DoesNotContain("java_method", map.Names);
        Assert.DoesNotContain("secret", map.Names);
        foreach (var name in map.Names)
        {
            Assert.False(SqlFunctionPolicy.IsDeniedByDefault(name), $"{name} is denylisted but mapped");
        }
    }

    [Fact]
    public void CallerAllowlist_CanOnlyNarrowTheMap()
    {
        // A caller list cannot add a function that has no rule.
        Assert.ThrowsAny<SecurityException>(() => Compile("SELECT some_extension_function(email) FROM orders", TargetSqlDialect.SqlServer,
            new HashSet<string> { "some_extension_function", "upper" }));
        Assert.ThrowsAny<SecurityException>(() => Compile("SELECT lower(email) FROM orders", TargetSqlDialect.SqlServer,
            new HashSet<string> { "upper" }));
        Assert.NotEmpty(Compile("SELECT upper(email) FROM orders", TargetSqlDialect.SqlServer, new HashSet<string> { "upper" }).Sql);
    }
}
