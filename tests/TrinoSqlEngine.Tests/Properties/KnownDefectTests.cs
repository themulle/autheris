using Xunit;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>
/// Defects the property gate found on the current compiler (WP-X0u). Each is a minimal reproduction. The strict test is skipped
/// with its finding ID until the compiler is fixed; a second test pins the current (fail-closed) behavior so that the fix is
/// noticed and the exemption in <see cref="Oracles.IsKnownDefectKd1"/> is removed. A skip is a quarantine in the sense of plan
/// 16.5 M-5 and needs the same security CODEOWNERS approval.
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class KnownDefectTests
{
    private static readonly CaseCtx Ctx = new(TargetSqlDialect.PostgreSql, true, "TNT1_acme", "POL1_eu");

    /// <summary>
    /// KD-1: a parenthesized query primary (<c>'(' queryNoWith ')'</c>) is valid Trino, but <c>SqlAstBuilder.VisitSetOperation</c>
    /// and the other query-body positions cast the <c>SelectStatement</c> that <c>VisitSubquery</c> returns to <c>QueryBody</c>.
    /// The request fails closed, but with an unhandled <see cref="InvalidCastException"/> instead of compiling or a typed rejection.
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

    [Theory(Skip = "KD-1: a parenthesized query primary throws InvalidCastException in SqlAstBuilder instead of compiling")]
    [MemberData(nameof(Kd1Inputs))]
    public void ParenthesizedQueryPrimary_CompilesOrIsRejectedWithATypedException(string sql)
    {
        var (compiled, error) = Oracles.TryCompile(ProductionCompiler.Compile, sql, Ctx, StatementPermissions.ReadOnly);
        Assert.True(compiled is not null || error is not null && Oracles.IsTypedRejection(error), $"unexpected {error?.GetType().FullName}");
    }

    [Theory]
    [MemberData(nameof(Kd1Inputs))]
    public void KD1_IsStillReproducible_SoTheExemptionCanBeRemovedWhenFixed(string sql)
    {
        var (compiled, error) = Oracles.TryCompile(ProductionCompiler.Compile, sql, Ctx, StatementPermissions.ReadOnly);

        // Documents the current behavior: fail closed, never an unsecured statement. When KD-1 is fixed this test fails; delete it,
        // the Skip above and Oracles.IsKnownDefectKd1.
        Assert.Null(compiled);
        Assert.NotNull(error);
        Assert.True(Oracles.IsKnownDefectKd1(error), error.GetType().FullName);
    }
}
