namespace TrinoSqlEngine.Tests.Ast;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>
/// CTE scoping of the governed compile path. Covered elsewhere (Compiler/CteShadowingTests): self-reference in a non-recursive WITH
/// (<c>NonRecursiveWith_SelfReference_IsRejected</c>), a qualified reference inside a CTE body
/// (<c>CteShadowingPhysicalName_StillSecuresTheQualifiedPhysicalTableInsideTheBody</c>) and quoted/unquoted case variants
/// (<c>QuotedCteVsUnquotedPhysical_CaseVariants_AlwaysSecured</c>, SEC C-02).
/// </summary>
[Trait("Category", "Security")]
public sealed class AstSecurityVisitorCteTests
{
    public static TheoryData<TargetSqlDialect> Dialects => new()
    {
        TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Cte_QualifiedName_IsNeverTreatedAsTheCte(TargetSqlDialect dialect)
    {
        // Even though a CTE named 'orders' exists, dbo.orders is qualified and must be the physical (secured) table
        var compiled = new ProbeEnv(dialect).Compile("WITH orders AS (SELECT 1 AS x) SELECT * FROM dbo.orders");

        Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        ProbeAsserts.TenantPredicates(compiled, 1);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Cte_OuterReference_UsesTheCte_WithoutAnyInjection(TargetSqlDialect dialect)
    {
        // virtual CTE without a physical table: nothing to secure
        var compiled = new ProbeEnv(dialect).Compile("WITH virtual_table AS (SELECT 1 AS col) SELECT * FROM virtual_table");

        Assert.DoesNotContain(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Empty(compiled.AppliedPredicates);
    }
}
