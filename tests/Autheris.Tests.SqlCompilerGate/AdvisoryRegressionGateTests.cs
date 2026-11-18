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
/// on every dialect, so the outcome no longer depends on the evaluation order the engine of that dialect picks. The compile needs no database.
/// </summary>
[Trait("Category", "Security")]
public sealed class AdvisoryRegressionGateTests
{
    private const string Probe = "SELECT 1 FROM orders WHERE 1 / (CASE WHEN id = 42 THEN 0 ELSE 1 END) = 1";

    private static CompileRequest Request(string dialect)
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
        var request = new GatePolicies(gate, schema, PolicyScenario.TenantConsentMask).AstRequest(StatementPermissions.ReadOnly);
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
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    [InlineData("databricks")]
    public void Ghsa_66xj_x8gx_regression(string dialect)
    {
        var engine = new FastSqlEngine();

        var refused = Should.Throw<SecurityException>(() => engine.Compile(Probe.AsMemory(), Request(dialect), CancellationToken.None));
        refused.Message.ShouldBe(RowPolicyErrorGuard.Message);

        Should.Throw<SecurityException>(() => engine.Compile("SELECT id FROM orders WHERE CAST(status AS INTEGER) = 1".AsMemory(), Request(dialect), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);
        Should.Throw<SecurityException>(() => engine.Compile("SELECT id FROM orders WHERE id % (id - 1) = 0".AsMemory(), Request(dialect), CancellationToken.None))
            .Message.ShouldBe(RowPolicyErrorGuard.Message);

        // control: statements without a data-dependent failure still compile (the Databricks case reuses the PostgreSQL catalog types, so it only proves the refusal)
        if (dialect != "databricks") engine.Compile("SELECT id FROM orders WHERE id > 3 AND id / 2 = 1".AsMemory(), Request(dialect), CancellationToken.None).Sql.ShouldNotBeNullOrEmpty();
    }
}
