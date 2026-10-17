using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>Compile helper for the client list tests (REQ-SQL-128): an engine, a permissive row filter and a tenant-scoped Orders table.</summary>
internal sealed class ClientListHarness
{
    public static readonly TargetSqlDialect[] Dialects =
    [
        TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.Sqlite, TargetSqlDialect.Oracle, TargetSqlDialect.DuckDb
    ];

    public static IEnumerable<object[]> DialectData() => Dialects.Select(d => new object[] { d });

    public FastSqlEngine Engine { get; } = new();

    public DictPolicyProvider RowFilters { get; } = new();

    public DictMaskProvider Masks { get; } = new();

    public ClientListHarness()
    {
        RowFilters.NoPolicy.Add(Orders);
        RowFilters.NoPolicy.Add(Entitlements);
        RowFilters.NoPolicy.Add(Lookup);
    }

    public static ClientListShapes Shapes(params (string Name, int Count)[] lists) =>
        ClientListShapes.Create(lists.ToDictionary(l => l.Name, l => l.Count));

    public CompileRequest Request(
        TargetSqlDialect dialect,
        ClientListShapes? shapes = null,
        StatementPermissions statements = StatementPermissions.ReadOnly,
        string tenant = "acme") => new()
    {
        TargetDialect = dialect,
        AllowExperimentalDialect = true,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = statements,
        ClientLists = shapes ?? ClientListShapes.Empty,
        Policy = new GovernancePolicy
        {
            RowFilters = RowFilters,
            Masks = Masks,
            Catalog = DmlCompileTests.CatalogFor(dialect),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String)
        }
    };

    public CompiledSql Compile(TargetSqlDialect dialect, string sql, ClientListShapes? shapes = null, StatementPermissions statements = StatementPermissions.ReadOnly) =>
        Engine.Compile(sql.AsMemory(), Request(dialect, shapes, statements), CancellationToken.None);

    public static List<BoundParameter> ListSlots(CompiledSql compiled, string name) =>
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.ClientNamed && p.ListIndex is not null
            && string.Equals(p.SourceName, name, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.ListIndex).ToList();
}
