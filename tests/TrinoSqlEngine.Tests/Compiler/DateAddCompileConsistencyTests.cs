using TrinoSqlEngine.Ast.Emit;
using System.Collections.Immutable;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// WP-X0-fix item 3: with <c>TranslateTrinoDateFunctions</c> the function map of the governed compiler and the generators agree.
/// <c>date_add</c> has a native translation on every governed dialect, so the compiler must accept it everywhere (the conformance
/// runner used the legacy per-dialect allowlist and reported a rejection that the production path never produced).
/// </summary>
public class DateAddCompileConsistencyTests
{
    private static readonly TableIdentity Orders = new("dbo", "Orders");

    private static readonly InMemoryTableCatalog Catalog = new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Ts", "datetime2")), "TenantId", 1)
    }, "dbo");

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void DateAdd_IsAcceptedByTheCompilerFunctionMap(TargetSqlDialect dialect)
    {
        var compiled = new FastSqlEngine().Compile("SELECT id FROM orders WHERE ts > date_add('day', -1, ts)".AsMemory(), new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            TranslateTrinoDateFunctions = true,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = Catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);
        Assert.NotNull(compiled.Sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void ConformanceBuild_UsesTheCompilerFunctionMap(TargetSqlDialect dialect)
    {
        // The statement the conformance gate emits must be the one Compile would emit: same function map.
        var statement = Conformance.ConformanceRunner.Prepare("SELECT date_add('day', 1, d) AS r FROM t", "date-translated", dialect);
        Assert.NotNull(statement);
    }
}
