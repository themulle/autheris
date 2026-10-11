using TrinoSqlEngine.Ast.Emit;
using System.Collections.Immutable;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0-fix item 2: Trino functions whose native spelling differs are rewritten to an exact native equivalent, or rejected with a
/// typed <see cref="SqlCompileNotSupportedException"/> when there is none. The execution proofs against the real engines are in
/// <c>Autheris.Tests.Integration</c> (<c>FunctionMapping_*</c>).
/// </summary>
public class FunctionMappingTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");

    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Email", "nvarchar(200)"),
            new CatalogColumn("Region", "nvarchar(20)")), "TenantId", 1)
    }, "dbo");

    private static string Sql(string expression, TargetSqlDialect dialect) =>
        new FastSqlEngine().Compile($"SELECT {expression} AS r FROM orders".AsMemory(), new CompileRequest
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
        }, CancellationToken.None).Sql;

    private static void AssertRejected(string expression, TargetSqlDialect dialect) =>
        Assert.Equal(SqlCompileNotSupportedReason.Construct, Assert.Throws<SqlCompileNotSupportedException>(() => Sql(expression, dialect)).Reason);

    // ---- SQL Server ----

    [Theory]
    [InlineData("substr(email, 7)", "SUBSTRING([email], @p0, @p1)")]
    [InlineData("substring(email, 7)", "SUBSTRING([email], @p0, @p1)")]
    [InlineData("substr(email, 7, 5)", "SUBSTRING([email], @p0, @p1)")]
    [InlineData("substring(email, 7, 5)", "SUBSTRING([email], @p0, @p1)")]
    [InlineData("replace(email, 'a')", "REPLACE([email], @p0, @p1)")]
    public void SqlServer_RewritesToTheNativeForm(string expression, string expected) =>
        Assert.Contains(expected, Sql(expression, TargetSqlDialect.SqlServer), StringComparison.Ordinal);

    [Theory]
    [InlineData("lpad(email, 5, '0')")]
    [InlineData("rpad(email, 5, '0')")]
    [InlineData("octet_length(email)")]
    public void SqlServer_NoExactEquivalent_IsRejectedWithATypedException(string expression) => AssertRejected(expression, TargetSqlDialect.SqlServer);

    // ---- PostgreSQL ----

    [Fact]
    public void PostgreSql_TwoArgumentReplace_RemovesTheSearchString() =>
        Assert.Contains("REPLACE(\"email\", $1, $2)", Sql("replace(email, 'a')", TargetSqlDialect.PostgreSql), StringComparison.Ordinal);

    // ---- Oracle ----

    [Theory]
    [InlineData("concat(email, region, email)", "CONCAT(CONCAT(\"EMAIL\", \"REGION\"), \"EMAIL\")")]
    [InlineData("concat(email, region, email, region)", "CONCAT(CONCAT(CONCAT(\"EMAIL\", \"REGION\"), \"EMAIL\"), \"REGION\")")]
    [InlineData("concat(email, region)", "CONCAT(\"EMAIL\", \"REGION\")")]
    [InlineData("substring(email, 7)", "SUBSTR(\"EMAIL\", :p1)")]
    [InlineData("substring(email, 7, 5)", "SUBSTR(\"EMAIL\", :p1, :p2)")]
    [InlineData("octet_length(email)", "LENGTHB(\"EMAIL\")")]
    [InlineData("char_length(email)", "LENGTH(\"EMAIL\")")]
    [InlineData("character_length(email)", "LENGTH(\"EMAIL\")")]
    [InlineData("log10(id)", "LOG(10, \"ID\")")]
    public void Oracle_RewritesToTheNativeForm(string expression, string expected) =>
        Assert.Contains(expected, Sql(expression, TargetSqlDialect.Oracle), StringComparison.Ordinal);

    // ---- the function map only lists Trino names the dialect has a rule for ----

    [Theory]
    [InlineData("arbitrary(email)", TargetSqlDialect.PostgreSql)]
    [InlineData("arbitrary(email)", TargetSqlDialect.SqlServer)]
    [InlineData("arbitrary(email)", TargetSqlDialect.Oracle)]
    [InlineData("arbitrary(email)", TargetSqlDialect.Sqlite)]
    [InlineData("approx_distinct(email)", TargetSqlDialect.PostgreSql)]
    [InlineData("approx_distinct(email)", TargetSqlDialect.SqlServer)]
    [InlineData("approx_distinct(email)", TargetSqlDialect.Oracle)]
    [InlineData("approx_distinct(email)", TargetSqlDialect.Sqlite)]
    [InlineData("approx_distinct(email)", TargetSqlDialect.DuckDb)]
    [InlineData("strpos(email, 'a')", TargetSqlDialect.Sqlite)]
    [InlineData("now()", TargetSqlDialect.SqlServer)]
    [InlineData("now()", TargetSqlDialect.Oracle)]
    [InlineData("now()", TargetSqlDialect.Sqlite)]
    [InlineData("date_trunc('day', email)", TargetSqlDialect.SqlServer)]
    [InlineData("date_add('day', 1, email)", TargetSqlDialect.PostgreSql)]
    [InlineData("date_add('day', 1, email)", TargetSqlDialect.SqlServer)]
    [InlineData("date_add('day', 1, email)", TargetSqlDialect.Oracle)]
    public void NameWithoutARuleInTheDialect_IsNotInTheFunctionMap(string expression, TargetSqlDialect dialect) =>
        Assert.Throws<System.Security.SecurityException>(() => Sql(expression, dialect));

    [Theory]
    [InlineData("arbitrary(email)", TargetSqlDialect.DuckDb)]
    [InlineData("strpos(email, 'a')", TargetSqlDialect.PostgreSql)]
    [InlineData("strpos(email, 'a')", TargetSqlDialect.SqlServer)]
    [InlineData("strpos(email, 'a')", TargetSqlDialect.Oracle)]
    [InlineData("now()", TargetSqlDialect.PostgreSql)]
    [InlineData("date_trunc('day', ts)", TargetSqlDialect.PostgreSql)]
    public void NameWithARuleInTheDialect_IsAccepted(string expression, TargetSqlDialect dialect) =>
        Assert.NotEmpty(Sql(expression.Replace("ts", "email", StringComparison.Ordinal), dialect));
}
