using DuckDB.NET.Data;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Issue #1038: Filter-first evaluation fence tests per dialect and strict refusals where fencing does not apply.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class EvaluationFenceTests
{
    private readonly FastSqlEngine _engine = new();

    private static PolicyPredicate RegionOrPolicy() => PolicyPredicate.Create(
        new BinaryExpression(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r1", SqlParameterType.String)),
            BinaryOperator.Or,
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_r2", SqlParameterType.String))),
        new Dictionary<string, PolicyValue>
        {
            ["__pol_r1"] = new("Nord", SqlParameterType.String),
            ["__pol_r2"] = new("Ost", SqlParameterType.String)
        });

    private static CompileRequest Request(TargetSqlDialect dialect, StatementPermissions permissions = StatementPermissions.ReadOnly)
    {
        var rowFilters = new DictPolicyProvider();
        rowFilters.Predicates[Orders] = RegionOrPolicy();
        rowFilters.NoPolicy.Add(Entitlements);
        rowFilters.NoPolicy.Add(Lookup);

        return new CompileRequest
        {
            TargetDialect = dialect,
            Statements = permissions,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowedFunctions = SqlFunctionAllowlists.Build(dialect, null),
            AllowExperimentalDialect = true,
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "OFFSET 0")]
    [InlineData(TargetSqlDialect.SqlServer, "ORDER BY (SELECT NULL) OFFSET 0 ROWS")]
    [InlineData(TargetSqlDialect.Oracle, "AND ROWNUM >= 1")]
    public void FencedDialects_EmitExpectedEvaluationBarrier_OnRowPolicyTableOnly(TargetSqlDialect dialect, string expectedBarrier)
    {
        const string sql = """
            SELECT SUM(o.Amount * 2) AS doubled
            FROM Orders o
            JOIN Lookup l ON o.Status = l.Code
            """;

        var compiled = _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None);

        Assert.Contains(expectedBarrier, compiled.Sql, StringComparison.Ordinal);

        // Oracle OR predicate inside policy must remain parenthesized before AND ROWNUM >= 1.
        if (dialect == TargetSqlDialect.Oracle)
        {
            Assert.Contains("WHERE ((", compiled.Sql, StringComparison.Ordinal);
            Assert.Contains(") AND ROWNUM >= 1)", compiled.Sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void FencedDialects_AllowCallerExpressions_Arithmetic_Cast_Functions_Case_Aggregates(TargetSqlDialect dialect)
    {
        string[] queries =
        [
            "SELECT SUM(Amount * 2), AVG(Amount / 100), Id + 1 FROM Orders GROUP BY Id",
            "SELECT CAST(Status AS integer), ROUND(Amount, 1), ABS(Id) FROM Orders",
            "SELECT CASE WHEN Amount > 0 THEN Amount / 2 ELSE 0 END FROM Orders WHERE sqrt(Amount) > 1",
            "WITH src AS (SELECT Id, Amount FROM Orders) SELECT Id, SUM(Amount * 3) OVER (PARTITION BY Id) FROM src",
            "SELECT o.Id, o.Amount / 10 FROM Orders o JOIN Entitlements e ON o.Id = e.OrderId WHERE o.Amount > 5"
        ];

        foreach (var sql in queries)
        {
            var compiled = _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(compiled.Sql));
        }
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void UnfencedDialects_RefuseRaisingCallerExpressions_WithCodeAndSafeHint(TargetSqlDialect dialect)
    {
        var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            _engine.Compile("SELECT Amount * 2 FROM Orders".AsMemory(), Request(dialect), CancellationToken.None));

        Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
        Assert.Contains("TRY_CAST", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("NULLIF", ex.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Amount", ex.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Orders", ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AnsiDialect_IsRejectedFailClosed()
    {
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            _engine.Compile("SELECT Amount * 2 FROM Orders".AsMemory(), Request(TargetSqlDialect.Ansi), CancellationToken.None));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void FencedDialects_StillRefuseDml_Coercions_AndConstantHazards(TargetSqlDialect dialect)
    {
        var dmlRequest = Request(dialect, StatementPermissions.Delete).WithTestWritePermit("DELETE FROM Orders WHERE Amount / Id > 1");
        Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            _engine.Compile("DELETE FROM Orders WHERE Amount / Id > 1".AsMemory(), dmlRequest, CancellationToken.None));

        string[] alwaysRefusedReads =
        [
            "SELECT Id FROM Orders WHERE Id = '5'",
            "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN sqrt(-1) ELSE 0 END = 0",
            "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN CAST('abc' AS integer) ELSE 0 END = 0",
            "SELECT Id FROM Orders WHERE 9223372036854775807 + 1 > 0"
        ];

        foreach (var sql in alwaysRefusedReads)
        {
            var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
                _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None));
            Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FencedDialect_ClientBindFamilyMismatch_ThrowsRowPolicyExpressionRejectedException()
    {
        var compiled = _engine.Compile(
            "SELECT Id FROM Orders WHERE Id = __param_id".AsMemory(),
            Request(TargetSqlDialect.PostgreSql),
            CancellationToken.None);

        using var cmd = new DuckDBCommand();
        var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(
                cmd,
                compiled,
                new Dictionary<string, object?> { ["id"] = "not-an-integer" }));

        Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        Assert.Contains("TRY_CAST", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("NULLIF", ex.Hint, StringComparison.Ordinal);
    }
}
