namespace Autheris.Tests.SqlCompilerGate;

using System.Security;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// Regression of GHSA-66xj-4459-x8gx (details: private advisory), one case per target dialect. The statement is refused at compile time
/// on every dialect. The compile needs no database.
/// </summary>
[Trait("Category", "Security")]
public sealed class AdvisoryRegressionGateTests
{
    private const string Probe = "SELECT 1 FROM orders WHERE 1 / (id - id + 1) = 1";

    private static CompileRequest Request(string dialect, PolicyScenario scenario, string? statementClass = null, string? sql = null)
    {
        var (gate, target) = dialect switch
        {
            "postgresql" => (GateDialect.PostgreSql, TargetSqlDialect.PostgreSql),
            "sqlserver" => (GateDialect.SqlServer, TargetSqlDialect.SqlServer),
            "oracle" => (GateDialect.Oracle, TargetSqlDialect.Oracle),
            "databricks" => (GateDialect.PostgreSql, TargetSqlDialect.Databricks),
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, null)
        };
        var schema = gate switch { GateDialect.SqlServer => "dbo", GateDialect.Oracle => "GATE_APP", _ => "public" };
        var request = new GatePolicies(gate, schema, scenario).AstRequest(statementClass is null ? StatementPermissions.ReadOnly : GatePolicies.PermissionsOf(statementClass), statementClass, sql);
        return target == request.TargetDialect
            ? request
            : request with
            {
                TargetDialect = target,
                TokenGuards = SqlTokenSecurityOptions.ForDialect(target),
                AllowedFunctions = SqlFunctionAllowlists.Build(target, null)
            };
    }

    [Theory]
    [InlineData("postgresql", "case_01")]
    [InlineData("sqlserver", "case_01")]
    [InlineData("oracle", "case_01")]
    [InlineData("databricks", "case_01")]
    [InlineData("postgresql", "case_02")]
    [InlineData("sqlserver", "case_02")]
    [InlineData("oracle", "case_02")]
    [InlineData("databricks", "case_02")]
    public void Ghsa_66xj_x8gx_regression(string dialect, string item)
    {
        var scenario = item == "case_01" ? PolicyScenario.TenantConsentMask : PolicyScenario.Tenant;
        var engine = new FastSqlEngine();

        var refused = Should.Throw<SecurityException>(() => engine.Compile(Probe.AsMemory(), Request(dialect, scenario), CancellationToken.None));
        refused.Message.ShouldBe(RowPolicyErrorGuard.Message);

        Should.Throw<SecurityException>(() => engine.Compile("SELECT id FROM orders WHERE CAST(status AS INTEGER) = 1".AsMemory(), Request(dialect, scenario), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
        Should.Throw<SecurityException>(() => engine.Compile("SELECT id FROM orders WHERE id % (id - 1) = 0".AsMemory(), Request(dialect, scenario), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);

        // control: statements without a data-dependent failure still compile (the Databricks case reuses the PostgreSQL catalog types, so it only proves the refusal)
        if (dialect != "databricks") engine.Compile("SELECT id FROM orders WHERE id > 3 AND id / 2 = 1".AsMemory(), Request(dialect, scenario), CancellationToken.None).Sql.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("postgresql", "case_03", "SELECT id FROM orders WHERE sqrt(id) > 1")]
    [InlineData("sqlserver", "case_03", "SELECT id FROM orders WHERE sqrt(id) > 1")]
    [InlineData("oracle", "case_03", "SELECT id FROM orders WHERE sqrt(id) > 1")]
    [InlineData("databricks", "case_03", "SELECT id FROM orders WHERE sqrt(id) > 1")]
    [InlineData("postgresql", "case_04", "SELECT id FROM orders WHERE power(id, 2) = 4")]
    [InlineData("sqlserver", "case_04", "SELECT id FROM orders WHERE power(id, 2) = 4")]
    [InlineData("oracle", "case_04", "SELECT id FROM orders WHERE power(id, 2) = 4")]
    [InlineData("databricks", "case_04", "SELECT id FROM orders WHERE power(id, 2) = 4")]
    [InlineData("postgresql", "case_05", "SELECT id FROM orders WHERE id + 1 > 3")]
    [InlineData("sqlserver", "case_05", "SELECT id FROM orders WHERE id + 1 > 3")]
    [InlineData("oracle", "case_05", "SELECT id FROM orders WHERE id + 1 > 3")]
    [InlineData("databricks", "case_05", "SELECT id FROM orders WHERE id + 1 > 3")]
    public void Regression_allowlist_case(string dialect, string item, string sql)
    {
        Assert.NotNull(item);
        var engine = new FastSqlEngine();

        Should.Throw<SecurityException>(() => engine.Compile(sql.AsMemory(), Request(dialect, PolicyScenario.Tenant), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
    }

    [Theory]
    [InlineData("postgresql", "case_06", "update", "UPDATE orders SET status = 'x' WHERE id + 1 > 3")]
    [InlineData("sqlserver", "case_06", "update", "UPDATE orders SET status = 'x' WHERE id + 1 > 3")]
    [InlineData("oracle", "case_06", "update", "UPDATE orders SET status = 'x' WHERE id + 1 > 3")]
    [InlineData("postgresql", "case_07", "delete", "DELETE FROM orders WHERE 1 / (id - id + 1) = 1")]
    [InlineData("sqlserver", "case_07", "delete", "DELETE FROM orders WHERE 1 / (id - id + 1) = 1")]
    [InlineData("oracle", "case_07", "delete", "DELETE FROM orders WHERE 1 / (id - id + 1) = 1")]
    [InlineData("postgresql", "case_08", "update", "UPDATE orders SET status = lower(status) || 'x' WHERE id > 3")]
    [InlineData("sqlserver", "case_08", "update", "UPDATE orders SET status = lower(status) || 'x' WHERE id > 3")]
    [InlineData("oracle", "case_08", "update", "UPDATE orders SET status = lower(status) || 'x' WHERE id > 3")]
    public void Regression_dml_case(string dialect, string item, string statementClass, string sql)
    {
        Assert.NotNull(item);
        var engine = new FastSqlEngine();

        Should.Throw<SecurityException>(() => engine.Compile(sql.AsMemory(), Request(dialect, PolicyScenario.Tenant, statementClass, sql), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
    }

    [Theory]
    [InlineData("postgresql", "case_09", "SELECT id FROM orders WHERE status = 5")]
    [InlineData("sqlserver", "case_09", "SELECT id FROM orders WHERE id = '5'")]
    [InlineData("oracle", "case_09", "SELECT id FROM orders WHERE id = '5'")]
    [InlineData("postgresql", "case_10", "SELECT id FROM orders WHERE coalesce(id, 'x') = 1")]
    [InlineData("oracle", "case_10", "SELECT id FROM orders WHERE coalesce(status, id) = 'x'")]
    public void Regression_coercion_case(string dialect, string item, string sql)
    {
        Assert.NotNull(item);
        var engine = new FastSqlEngine();

        Should.Throw<SecurityException>(() => engine.Compile(sql.AsMemory(), Request(dialect, PolicyScenario.Tenant), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
    }

    [Theory]
    [InlineData("postgresql", "case_11", "SELECT id FROM orders WHERE id = (SELECT max(status) FROM orders)")]
    [InlineData("sqlserver", "case_11", "SELECT id FROM orders WHERE id = (SELECT max(status) FROM orders)")]
    [InlineData("oracle", "case_11", "SELECT id FROM orders WHERE id = (SELECT max(status) FROM orders)")]
    [InlineData("databricks", "case_11", "SELECT id FROM orders WHERE id = (SELECT max(status) FROM orders)")]
    [InlineData("postgresql", "case_12", "SELECT id FROM orders WHERE id IN (SELECT status FROM orders)")]
    [InlineData("sqlserver", "case_12", "SELECT id FROM orders WHERE id IN (SELECT status FROM orders)")]
    [InlineData("oracle", "case_12", "SELECT id FROM orders WHERE id IN (SELECT status FROM orders)")]
    [InlineData("databricks", "case_12", "SELECT id FROM orders WHERE id IN (SELECT status FROM orders)")]
    [InlineData("postgresql", "case_13", "SELECT id FROM orders UNION ALL SELECT status FROM orders")]
    [InlineData("sqlserver", "case_13", "SELECT id FROM orders UNION ALL SELECT status FROM orders")]
    [InlineData("oracle", "case_13", "SELECT id FROM orders UNION ALL SELECT status FROM orders")]
    [InlineData("databricks", "case_13", "SELECT id FROM orders UNION ALL SELECT status FROM orders")]
    public void Regression_subquery_coercion_case(string dialect, string item, string sql)
    {
        Assert.NotNull(item);
        var engine = new FastSqlEngine();

        Should.Throw<SecurityException>(() => engine.Compile(sql.AsMemory(), Request(dialect, PolicyScenario.Tenant), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
    }
}
