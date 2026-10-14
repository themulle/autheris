using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// The bind limit at the production capability value, probed with a real multi-row INSERT that needs one value more than the limit
/// (up to 65,535 binds). These are the probes that are CPU heavy, so they run one at a time (never next to another test class), on an
/// engine without a parse budget and with the compile budget at its maximum: the only way to fail is the limit itself. The boundary
/// logic is covered deterministically with a small capability table in <c>DmlCompileTests</c> and <c>LimitBoundaryProperties</c>.
/// </summary>
[Collection(ProductionLimitProbes.Name)]
[Trait("Category", "SqlGate")]
public class BindLimitProductionProbeTests
{
    public static IEnumerable<object[]> DialectData() => DmlCompileTests.Dialects.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(DialectData))]
    public void Insert_MultiRow_OverTheProductionBindLimit_IsRejected_WithTheTypedError(TargetSqlDialect dialect)
    {
        var engine = LimitProbeSupport.Engine(maxQueryLength: 8_000_000);   // the text guard (64 K characters) would otherwise fire first for the larger dialects
        var rowFilters = new DictPolicyProvider();
        rowFilters.NoPolicy.Add(Orders);
        var request = new CompileRequest
        {
            TargetDialect = dialect,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = StatementPermissions.Insert,
            CompileTimeout = CompileLimits.MaxCompileTimeout,   // the statement must reach the bind limit; the 2 s default budget would be a load-dependent failure
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = DmlCompileTests.CatalogFor(dialect),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
                Dml = DmlGuardOptions.Strict
            }
        };
        int max = DialectCapabilityTable.Default.Get(dialect).MaxBindParameters;
        int rows = (max / 2) + 5;   // id + status per row; the tenant marker is shared
        var sql = "INSERT INTO orders (id, tenantid, status) VALUES " +
                  string.Join(", ", Enumerable.Range(1, rows).Select(i => $"({i}, 'acme', 's{i}')"));

        var ex = Assert.Throws<SqlLimitExceededException>(() => engine.Compile(sql.AsMemory(), request, CancellationToken.None));

        Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
        Assert.Equal(max, ex.Maximum);
    }
}
