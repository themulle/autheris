using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0-fix item 6: constructs a dialect cannot emit under the bind-everything rule are rejected with the typed
/// <see cref="SqlCompileNotSupportedException"/> before emission, not through the emitted-SQL invariant check.
/// </summary>
[Trait("Category", "SqlGate")]
public class TypedRejectionTests
{
    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(new TableIdentity("dbo", "Orders"), ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("D", "date"),
            new CatalogColumn("Ts", "datetime2"), new CatalogColumn("Region", "nvarchar(20)")), "TenantId", 1)
    }, "dbo");

    private static string Sql(string sql, TargetSqlDialect dialect, bool translateDates = false) =>
        new FastSqlEngine().Compile(sql.AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            TranslateTrinoDateFunctions = translateDates,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = Catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None).Sql;

    private static void AssertTypedRejection(string sql, TargetSqlDialect dialect, bool translateDates = false)
    {
        var ex = Assert.Throws<SqlCompileNotSupportedException>(() => Sql(sql, dialect, translateDates));
        Assert.Equal(SqlCompileNotSupportedReason.Construct, ex.Reason);
    }

    [Theory]
    [InlineData("SELECT INTERVAL '1' DAY AS r FROM orders")]
    [InlineData("SELECT INTERVAL '2' MONTH AS r FROM orders")]
    [InlineData("SELECT INTERVAL '3' YEAR AS r FROM orders")]
    public void Oracle_IntervalLiteral(string sql) => AssertTypedRejection(sql, TargetSqlDialect.Oracle);

    // The strict token guards of Compile already reject the bracket tokens (ParseCanceledException); the generator itself must reject
    // them with the typed exception as well (conformance builds the statement without token guards).
    [Theory]
    [InlineData("SELECT a[1] AS r FROM t")]
    [InlineData("SELECT ARRAY[1, 2, 3] AS r FROM t")]
    public void SqlServer_ArraysAndSubscripts(string sql) =>
        Assert.Equal(nameof(SqlCompileNotSupportedException), Conformance.ConformanceRunner.Run(sql, "operator", TargetSqlDialect.SqlServer).ExceptionType);

    [Theory]
    [InlineData("SELECT DATE '2024-01-15' AS r FROM orders")]
    [InlineData("SELECT TIMESTAMP '2024-01-15 10:30:00' AS r FROM orders")]
    [InlineData("SELECT TIME '10:30:00' AS r FROM orders")]
    [InlineData("SELECT id FROM orders WHERE d = DATE '2024-01-15'")]
    [InlineData("SELECT EXTRACT(year FROM d) AS r FROM orders")]
    [InlineData("SELECT EXTRACT(dow FROM d) AS r FROM orders")]
    [InlineData("SELECT localtime AS r FROM orders")]
    [InlineData("SELECT localtimestamp AS r FROM orders")]
    [InlineData("SELECT id FROM orders ORDER BY id OFFSET 5")]
    [InlineData("SELECT id FROM orders OFFSET 0 ROWS")]
    public void Sqlite_ConstructsWithoutABoundForm(string sql) => AssertTypedRejection(sql, TargetSqlDialect.Sqlite);

    [Theory]
    [InlineData("SELECT date_trunc('day', ts) AS r FROM orders")]
    [InlineData("SELECT date_add('day', 1, d) AS r FROM orders")]
    [InlineData("SELECT d + INTERVAL '1' DAY AS r FROM orders")]
    public void Sqlite_DateTranslation(string sql) => AssertTypedRejection(sql, TargetSqlDialect.Sqlite, translateDates: true);
}
