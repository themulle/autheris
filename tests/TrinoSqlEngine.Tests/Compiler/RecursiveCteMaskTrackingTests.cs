using System.Collections.Frozen;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the masked column tracking through recursive CTEs: the output column a masked value reaches is found
/// by position in every member (anchor, middle and recursive members), through expressions, wildcards mark every output column, and
/// output names that cannot be resolved reject the statement (fail closed) instead of letting a masked value through.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class RecursiveCteMaskTrackingTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public RecursiveCteMaskTrackingTests()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private CompiledSql Compile(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = dialect,
        AllowExperimentalDialect = true,
        AllowRecursiveCte = true,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            TablesWithMaskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orders", "dbo.Orders" }
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT 2, email FROM orders UNION ALL SELECT n + 1, 'x' FROM r WHERE n < 3) ")]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT 2, 'x' UNION ALL SELECT r.n + 1, o.email FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ")]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT id, email FROM orders UNION ALL SELECT 2, 'x' UNION ALL SELECT n + 1, 'x' FROM r WHERE n < 3) ")]
    [InlineData("WITH RECURSIVE r (s, n) AS (SELECT email, 1 FROM orders UNION ALL SELECT 'x', n + 1 FROM r WHERE n < 3) ")]
    [InlineData("WITH RECURSIVE r (s, n) AS (SELECT 'x', 1 UNION ALL SELECT o.email, r.n + 1 FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ")]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT r.n + 1, coalesce(o.email, 'y') FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ")]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT r.n + 1, upper(o.email) FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ")]
    [InlineData("WITH RECURSIVE r (n, s) AS (SELECT 1, 'x' UNION ALL SELECT r.n + 1, CASE WHEN r.n > 0 THEN o.email END FROM r JOIN orders o ON o.id = r.n WHERE r.n < 3) ")]
    [InlineData("WITH RECURSIVE r AS (SELECT 1 AS n, 'x' AS s UNION ALL SELECT 2, email FROM orders UNION ALL SELECT n + 1, s FROM r WHERE n < 3) ")]
    [InlineData("WITH RECURSIVE r AS (SELECT id AS n, 'x' AS s FROM orders UNION ALL SELECT r.n + 1, e.email FROM r JOIN (SELECT id, email FROM orders) e ON e.id = r.n WHERE r.n < 3) ")]
    public void MaskedValueAtAnyMemberPosition_MarksThatOutputColumn(string head)
    {
        // the masked output column is never usable in a filter, a sort or an aggregate; the clear one still is
        Assert.Throws<SecurityException>(() => Compile(head + "SELECT n FROM r WHERE s = 'x'"));
        Assert.Throws<SecurityException>(() => Compile(head + "SELECT n FROM r ORDER BY s"));
        Assert.Throws<MaskedAggregationException>(() => Compile(head + "SELECT max(s) FROM r"));
        Assert.NotNull(Compile(head + "SELECT n FROM r WHERE n = 1"));
        Assert.NotNull(Compile(head + "SELECT max(n) FROM r"));
    }

    [Fact]
    public void WildcardMember_MarksEveryOutputColumn()
    {
        const string Head = "WITH RECURSIVE r (a, b, c, d, e, f) AS (SELECT 1, 'x', 'x', 'x', 1, 'x' UNION ALL SELECT o.* FROM orders o CROSS JOIN r WHERE o.id < 3) ";

        Assert.Throws<SecurityException>(() => Compile(Head + "SELECT a FROM r WHERE a = 1"));
        Assert.Throws<SecurityException>(() => Compile(Head + "SELECT a FROM r WHERE d = 'x'"));
        Assert.Throws<SecurityException>(() => Compile(Head + "SELECT a FROM r ORDER BY e"));
        Assert.Throws<MaskedAggregationException>(() => Compile(Head + "SELECT max(a) FROM r"));
        Assert.Throws<MaskedAggregationException>(() => Compile(Head + "SELECT count(f) FROM r"));
    }

    [Fact]
    public void WildcardMemberWithoutNamedOutputs_IsRejected()
    {
        const string Sql = "WITH RECURSIVE r AS (SELECT * FROM orders UNION ALL SELECT o.* FROM orders o CROSS JOIN r WHERE o.id < 3) SELECT id FROM r";

        var ex = Assert.Throws<SecurityException>(() => Compile(Sql));
        Assert.Equal("A masked column in a recursive CTE could not be tracked.", ex.Message);
    }

    [Fact]
    public void MaskedAnchorWithNamesFromTheAnchor_IsTrackedByName()
    {
        const string Head = "WITH RECURSIVE r AS (SELECT id AS n, email AS s FROM orders UNION ALL SELECT n + 1, s FROM r WHERE n < 3) ";

        Assert.Throws<SecurityException>(() => Compile(Head + "SELECT n FROM r WHERE s = 'x'"));
        Assert.NotNull(Compile(Head + "SELECT n FROM r WHERE n = 1"));
    }

    [Fact]
    public void MaskedAnchorWithUnaliasedColumn_KeepsItsSimpleName()
    {
        const string Head = "WITH RECURSIVE r AS (SELECT id, email FROM orders UNION ALL SELECT id + 1, email FROM r WHERE id < 3) ";

        Assert.Throws<SecurityException>(() => Compile(Head + "SELECT id FROM r WHERE email = 'x'"));
        Assert.NotNull(Compile(Head + "SELECT id FROM r WHERE id = 1"));
    }

    [Fact]
    public void NoMaskedColumnAnywhere_IsNotRestricted()
    {
        const string Head = "WITH RECURSIVE r (n, s) AS (SELECT id, region FROM orders UNION ALL SELECT n + 1, s FROM r WHERE n < 3) ";

        Assert.NotNull(Compile(Head + "SELECT n FROM r WHERE s = 'x'"));
        Assert.NotNull(Compile(Head + "SELECT max(s) FROM r"));
    }
}
