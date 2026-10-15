using TrinoSqlEngine.Ast.Builder;
using Xunit;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// Defects the property gate found on the compiler (WP-X0u), kept as minimal reproductions of the fixed behavior.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class KnownDefectTests
{
    private static readonly CaseCtx Ctx = new(TargetSqlDialect.PostgreSql, true, "TNT1_acme", "POL1_eu");

    /// <summary>
    /// KD-1: a parenthesized query primary (<c>'(' queryNoWith ')'</c>) is valid Trino, but the AST has no node that keeps a
    /// <c>SelectStatement</c> (with its own ORDER BY / LIMIT) in a query-body position, and the generators do not parenthesize
    /// set-operation operands. The builder therefore rejects it with a typed <see cref="AstBuildException"/> (it used to throw an
    /// unhandled <see cref="InvalidCastException"/>).
    /// </summary>
    public static TheoryData<string> Kd1Inputs => new()
    {
        "(SELECT id FROM orders)",
        "(SELECT id AS c0 FROM orders) UNION ALL (SELECT id AS c0 FROM entitlements)",
        "SELECT id FROM orders UNION ALL (SELECT id FROM orders)",
        "SELECT * FROM ((VALUES 1, 2)) AS v (c0)",
        "SELECT * FROM ((SELECT id FROM orders)) AS v",
        "WITH c AS ((SELECT id FROM orders)) SELECT * FROM c"
    };

    [Theory]
    [MemberData(nameof(Kd1Inputs))]
    public void ParenthesizedQueryPrimary_IsRejectedWithATypedException(string sql)
    {
        var (compiled, error) = Oracles.TryCompile(ProductionCompiler.Compile, sql, Ctx, StatementPermissions.ReadOnly);
        Assert.Null(compiled);
        Assert.IsType<AstBuildException>(error);
    }

    /// <summary>
    /// KD-2: field access on a non-column base (<c>'text'."col"</c>, <c>lower(x).y</c>) used to be emitted as a column reference
    /// whose qualifier was the <c>ToString()</c> of the base AST node, so a string literal reached the SQL text as a quoted
    /// identifier (found by <c>GrammarFuzzProperties</c>, seed 1784412052261209272). The builder rejects it with a typed
    /// <see cref="AstBuildException"/>.
    /// </summary>
    public static TheoryData<string> Kd2Inputs => new()
    {
        "SELECT v.* FROM (VALUES (1, 2)) AS v (c0, c1) WHERE 'LIT1_x' IN ('LIT2_v'.\"quoted\", 'LIT3_v')",
        "SELECT 'LIT2_v'.\"quoted\" FROM orders",
        "SELECT id FROM orders WHERE 'LIT2_v'.status = 'open'",
        "SELECT 42.5.x FROM orders",
        "SELECT lower(status).x FROM orders"
    };

    [Theory]
    [MemberData(nameof(Kd2Inputs))]
    public void DereferenceOfANonColumnExpression_IsRejectedWithATypedException(string sql)
    {
        var (compiled, error) = Oracles.TryCompile(ProductionCompiler.Compile, sql, Ctx, StatementPermissions.ReadOnly);
        Assert.Null(compiled);
        Assert.IsType<AstBuildException>(error);
    }
}
