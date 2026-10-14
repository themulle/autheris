using System.Collections.Frozen;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// SR-L-24 (WP-SEC2-07): a bare whole-row reference in a SELECT list over a table with masked columns is rejected on the SELECT path.
/// The projection rewrite already substitutes mask expressions, so these forms did not return raw data; the guard is the explicit,
/// verifier-backed second line of defence.
/// </summary>
[Trait("Category", "SqlGate")]
public class SelectWholeRowMaskTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();

    public SelectWholeRowMaskTests()
    {
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _rowFilters.NoPolicy.Add(Orders);
    }

    private CompiledSql Compile(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer)
    {
        var request = new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = _rowFilters,
                Masks = _masks,
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
        return _engine.Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    [Theory]
    [InlineData("SELECT orders FROM orders")]
    [InlineData("SELECT o FROM orders o")]
    [InlineData("SELECT to_json(o) FROM orders o")]
    [InlineData("SELECT CAST(o AS varchar(100)) FROM orders o")]
    [InlineData("SELECT id, (SELECT o) FROM orders o")]
    [InlineData("SELECT o.* FROM orders o")]
    public void WholeRowOfMaskedTable_InSelectList_DoesNotCompileToRawData(string sql)
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql })
        {
            try
            {
                var compiled = Compile(sql, dialect);
                // Allowed forms (the qualified wildcard) must still project the mask, never the raw column.
                Assert.Contains("CAST(NULL AS", compiled.Sql);
            }
            catch (SecurityException)
            {
                // rejected by the whole-row guard (or an earlier gate): fail closed
            }
        }
    }

    [Theory]
    [InlineData("SELECT orders FROM orders")]
    [InlineData("SELECT o FROM orders o")]
    [InlineData("SELECT to_json(o) FROM orders o")]
    [InlineData("SELECT CAST(o AS varchar(100)) FROM orders o")]
    public void WholeRowOfMaskedTable_BareReference_IsRejected(string sql)
    {
        Assert.ThrowsAny<SecurityException>(() => Compile(sql));
    }

    [Fact]
    public void ExplicitColumns_OfMaskedTable_StillCompileWithMask()
    {
        var compiled = Compile("SELECT id, email FROM orders");

        Assert.Contains("CAST(NULL AS", compiled.Sql);
    }
}
