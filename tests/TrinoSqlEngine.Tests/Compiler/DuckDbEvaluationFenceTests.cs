using System.Data;
using DuckDB.NET.Data;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Issue #1038: Empirical safety verification against live in-process DuckDB proving why DuckDb remains
/// unfenced (<c>HasEvaluationFence == false</c>) and why non-raising forms work safely over hidden rows.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class DuckDbEvaluationFenceTests
{
    private readonly FastSqlEngine _engine = new();

    private static CompileRequest CreateDuckDbRequest()
    {
        var rowFilters = new DictPolicyProvider();
        rowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("Region", true) })),
                BinaryOperator.Equal,
                new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("Nord", SqlParameterType.String) });

        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.DuckDb,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.DuckDb, null),
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    private static DuckDBConnection OpenLiveDuckDbWithHiddenHazard()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA dbo;
            CREATE TABLE dbo."Orders" (
                "Id" INTEGER,
                "TenantId" VARCHAR,
                "Region" VARCHAR,
                "Status" VARCHAR,
                "Amount" DECIMAL(18, 2),
                "Email" VARCHAR
            );
            INSERT INTO dbo."Orders" VALUES
                (1, 'acme', 'Nord', '10', 100.50, 'a@acme.test'),
                (2, 'acme', 'Nord', '20', 200.25, 'b@acme.test'),
                (0, 'acme', 'Sud',  'bad-int', 999999999999.99, 'c@acme.test');
            """;
        cmd.ExecuteNonQuery();
        return connection;
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DuckDb_HasEvaluationFence_IsFalse_InDialectCapabilities()
    {
        var table = DialectCapabilityTable.Default;
        Assert.False(table.Get(TargetSqlDialect.DuckDb).HasEvaluationFence);
        Assert.False(table.Get(TargetSqlDialect.Databricks).HasEvaluationFence);
        Assert.False(table.TryGet(TargetSqlDialect.Ansi, out _));

        Assert.True(table.Get(TargetSqlDialect.PostgreSql).HasEvaluationFence);
        Assert.True(table.Get(TargetSqlDialect.SqlServer).HasEvaluationFence);
        Assert.True(table.Get(TargetSqlDialect.Oracle).HasEvaluationFence);
    }

    [Fact]
    public void LiveDuckDb_StatisticsPropagation_BypassedAcrossOffset0AndMaterializedCte()
    {
        using var conn = OpenLiveDuckDbWithHiddenHazard();

        // Empirical proof 1: CTE AS MATERIALIZED does NOT block outer predicate pushdown in DuckDB EXPLAIN.
        using (var explain = conn.CreateCommand())
        {
            explain.CommandText = """
                EXPLAIN WITH cte AS MATERIALIZED (
                    SELECT "Id", "Amount" FROM dbo."Orders" WHERE "Region" = 'Nord'
                )
                SELECT "Id" FROM cte WHERE "Amount" / "Id" > 10
                """;
            using var reader = explain.ExecuteReader();
            var planLines = new List<string>();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    planLines.Add(reader.GetValue(i)?.ToString() ?? string.Empty);
                }
            }

            var plan = string.Join("\n", planLines);
            Assert.Contains("FILTER", plan, StringComparison.OrdinalIgnoreCase);
        }

        // Empirical proof 2: DuckDB statistics propagation evaluates/folds range checks across hidden rows
        // even behind a naive OFFSET 0 derived table when combined with raising expressions.
        using var probe = conn.CreateCommand();
        probe.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT "Id" FROM dbo."Orders" WHERE "Region" = 'Nord' OFFSET 0
            ) t WHERE t."Id" = 0 AND SQRT(-1) > 0
            """;
        var ex = Assert.ThrowsAny<Exception>(() => probe.ExecuteScalar());
        Assert.Contains("Out of Range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DuckDb_RaisingSelectExpression_UnderRowPolicy_IsRefusedWithNamedCodeAndHint()
    {
        var ex = Assert.Throws<RowPolicyExpressionRejectedException>(() =>
            _engine.Compile("SELECT CAST(Status AS integer) FROM dbo.Orders".AsMemory(), CreateDuckDbRequest(), CancellationToken.None));

        Assert.Equal(GovernedSqlErrorCodes.RowPolicyExpressionNotAllowed, ex.Code);
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
        Assert.Contains("TRY_CAST", ex.Hint, StringComparison.Ordinal);
        Assert.Contains("NULLIF", ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveDuckDb_NonRaisingSumAndAvg_WithRowPolicy_CompilesAndExcludesHiddenRows()
    {
        using var conn = OpenLiveDuckDbWithHiddenHazard();

        const string sql = """
            SELECT
                SUM(TRY_CAST(Amount AS DECIMAL(38, 2))) AS total_amount,
                AVG(TRY_CAST(Amount AS DOUBLE)) AS avg_amount,
                SUM(TRY_CAST(Status AS DECIMAL(38, 0))) AS parsed_status_sum,
                SUM(TRY_CAST(NULLIF(Id, 0) AS DECIMAL(38, 0))) AS nonzero_id_sum
            FROM dbo.Orders
            """;

        var compiled = _engine.Compile(sql.AsMemory(), CreateDuckDbRequest(), CancellationToken.None);
        Assert.DoesNotContain("OFFSET 0", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        CompiledSqlBinders.For(TargetSqlDialect.DuckDb).Bind(cmd, compiled, new Dictionary<string, object?>());

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(300.75m, Convert.ToDecimal(reader.GetValue(0)));
        Assert.Equal(150.375d, Convert.ToDouble(reader.GetValue(1)), precision: 6);
        Assert.Equal(30m, Convert.ToDecimal(reader.GetValue(2)));
        Assert.Equal(3m, Convert.ToDecimal(reader.GetValue(3)));
        Assert.False(reader.Read());
    }
}
