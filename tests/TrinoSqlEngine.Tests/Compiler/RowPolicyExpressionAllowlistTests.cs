using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>Allowlist of non-raising user expressions while a tenant predicate applies (issue 847).</summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class RowPolicyExpressionAllowlistTests
{
    private readonly FastSqlEngine _engine = new();

    private static CompileRequest Request(TargetSqlDialect dialect)
    {
        var rowFilters = new DictPolicyProvider();
        rowFilters.NoPolicy.Add(Orders);
        rowFilters.NoPolicy.Add(Lookup);
        return new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            AllowedFunctions = SqlFunctionAllowlists.Build(dialect, null),
            Policy = new GovernancePolicy
            {
                RowFilters = rowFilters,
                Masks = new DictMaskProvider(),
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    private CompiledSql Compile(string sql, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) => _engine.Compile(sql.AsMemory(), Request(dialect), CancellationToken.None);

    public static TheoryData<string, string> Refused() => new()
    {
        { "case_01", "SELECT Id FROM Orders WHERE Id + 1 > 3" },
        { "case_02", "SELECT Id FROM Orders WHERE Id * 2 > 3" },
        { "case_03", "SELECT Id FROM Orders WHERE Amount / Id > 3" },
        { "case_04", "SELECT Id FROM Orders WHERE Id % Id = 0" },
        { "case_05", "SELECT Id FROM Orders WHERE sqrt(Id) > 3" },
        { "case_06", "SELECT Id FROM Orders WHERE ln(Id) > 3" },
        { "case_07", "SELECT Id FROM Orders WHERE power(Id, 2) > 3" },
        { "case_08", "SELECT Id FROM Orders WHERE to_date(Status) IS NULL" },
        { "case_09", "SELECT Id FROM Orders WHERE date_parse(Status, '%Y') IS NULL" },
        { "case_10", "SELECT Id FROM Orders WHERE regexp_like(Status, 'a+')" },
        { "case_11", "SELECT Id FROM Orders WHERE json_parse(Status) IS NULL" },
        { "case_12", "SELECT Id FROM Orders WHERE element_at(Status, Id) = 'a'" },
        { "case_13", "SELECT Id FROM Orders WHERE exp(Id) > 1" },
        { "case_14", "SELECT Id FROM (SELECT 1 / (Id - Id + 1) AS Q, Id FROM Orders) AS d WHERE Q = 1" },
        { "case_15", "SELECT Id FROM Orders WHERE Id = '5'" },
        { "case_16", "SELECT Id FROM Orders WHERE CAST(Status AS INTEGER) = 1" },
        { "case_17", "SELECT Id FROM Orders WHERE (SELECT Id FROM Entitlements) = 1" },
        { "case_19", "SELECT Id FROM Orders WHERE abs(Id) > 3" },
        { "case_20", "SELECT Id FROM Orders GROUP BY Id HAVING sum(Id) > 3" },
        { "case_21", "SELECT Id FROM Orders WHERE -Id > 3" },
        { "case_22", "SELECT Id FROM Orders WHERE Id / 0 > 3" },
        { "case_23", "SELECT Id FROM Orders WHERE substring(Status, Id, 2) = 'a'" },
        { "case_24", "SELECT Id FROM Orders WHERE from_base(Status, 16) = 1" },
        { "case_25", "SELECT Id FROM Orders WHERE coalesce(Id, Status) = 1" },
        { "case_26", "SELECT Id FROM Orders WHERE CASE WHEN Id > 1 THEN Status ELSE Amount END = 'a'" },
        { "case_27", "SELECT Id FROM Orders WHERE Status || Region = 'x'" },
        { "case_28", "SELECT Id FROM Orders WHERE concat(Status, Region) = 'x'" },
        { "case_29", "SELECT Id FROM Orders WHERE year(Status) = 2020" },
        { "case_30", "SELECT Id FROM Orders WHERE Id / 0.5 > 3" },
        { "case_31", "SELECT sum(Amount) FROM Orders" },
        { "case_32", "SELECT Id + 1 FROM Orders" },
        { "case_33", "SELECT sqrt(Id), Id / Amount FROM Orders WHERE Id > 3" },
        { "case_34", "SELECT Id FROM (SELECT Status AS s, Id FROM Orders) AS d WHERE d.s = 5" },
        { "case_35", "SELECT Id FROM Orders WHERE Id = Status" },
        { "case_41", "SELECT abs(Id) FROM Orders" },
        { "case_42", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN sqrt(-1) ELSE 0 END = 0" },
        { "case_43", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN 9223372036854775807 + 1 ELSE 0 END = 0" },
        { "case_44", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN CAST('abc' AS integer) ELSE 0 END = 0" },
        { "case_45", "SELECT Id FROM Orders WHERE Id = 42 AND sqrt(-1) > 0" },
        { "case_46", "SELECT Id FROM Orders WHERE CAST(Amount AS integer) = 1" },
        { "case_37", "SELECT Id FROM Orders WHERE Id = (SELECT max(Status) FROM Orders)" },
        { "case_38", "SELECT Id FROM Orders WHERE Id IN (SELECT Status FROM Orders)" },
        { "case_39", "SELECT Id FROM Orders WHERE Id > ANY (SELECT Status FROM Orders)" },
        { "case_40", "SELECT Id FROM Orders UNION ALL SELECT Status FROM Orders" },
        { "case_36", "SELECT Id FROM Orders WHERE Id BETWEEN Amount AND Status" },
    };

    public static TheoryData<string, string> Allowed() => new()
    {
        { "case_01", "SELECT Id FROM Orders WHERE Id > 3 AND Status = 'open' OR Region <> 'EU'" },
        { "case_02", "SELECT Id FROM Orders WHERE Status IS NULL OR Email IS NOT NULL" },
        { "case_03", "SELECT Id FROM Orders WHERE Id IN (1, 2, 3)" },
        { "case_04", "SELECT Id FROM Orders WHERE Status LIKE 'op%'" },
        { "case_05", "SELECT Id FROM Orders WHERE Id BETWEEN 1 AND 10" },
        { "case_06", "SELECT Id FROM Orders WHERE coalesce(Status, 'x') = 'x' AND nullif(Region, 'EU') IS NULL" },
        { "case_07", "SELECT Id FROM Orders WHERE CASE WHEN Id > 3 THEN 1 ELSE 0 END = 1" },
        { "case_08", "SELECT Id FROM Orders WHERE lower(Status) = 'a' AND upper(Region) = 'B' AND length(Email) > 3" },
        { "case_09", "SELECT Id FROM Orders WHERE substring(Status, 1, 2) = 'ab'" },
        { "case_10", "SELECT count(*), min(Id), max(Id) FROM Orders" },
        { "case_11", "SELECT Id FROM Orders WHERE Id / 2 = 1 AND Id % 3 = 0" },
        { "case_12", "SELECT Id FROM Orders WHERE trim(Status) = 'a'" },
        { "case_13", "SELECT Id FROM Orders WHERE TRY_CAST(Status AS INTEGER) = 1" },
        { "case_14", "SELECT Id FROM Orders WHERE Amount > 1.5" },
        { "case_15", "WITH c AS (SELECT Id AS k, Status AS t FROM Orders) SELECT k FROM c WHERE k > 3 AND t = 'open'" },
        { "case_16", "SELECT Id, Status, count(*) FROM Orders WHERE Id > 3 GROUP BY Id, Status" },
        { "case_21", "SELECT Id FROM Orders WHERE Status = __param_p AND Region IN (__param_a, __param_b)" },
        { "case_17", "SELECT Id FROM Orders WHERE Id = (SELECT max(Id) FROM Orders) AND Id IN (SELECT Id FROM Entitlements)" },
        { "case_18", "SELECT Id FROM Orders UNION ALL SELECT Id FROM Entitlements" },
        { "case_19", "SELECT lower('A'), length('x') FROM Orders" },
        { "case_20", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN 1 + 2 * 3 ELSE 0 END = 0 AND CAST(Status AS varchar) = 'a'" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Refused_expression_under_tenant_predicate_is_rejected(string item, string sql)
    {
        Assert.NotNull(item);
        Assert.Throws<SecurityException>(() => Compile(sql));
    }

    [Theory]
    [InlineData("case_01", "SELECT Id FROM Orders WHERE Id + 1 > 3")]
    [InlineData("case_02", "SELECT Id FROM Orders WHERE sqrt(Id) > 3")]
    [InlineData("case_03", "SELECT Id FROM Orders WHERE Id = '5'")]
    public void Refusal_uses_the_fixed_message(string item, string sql)
    {
        Assert.NotNull(item);
        var ex = Assert.Throws<SecurityException>(() => Compile(sql));
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowed_expression_under_tenant_predicate_compiles(string item, string sql)
    {
        Assert.NotNull(item);
        Assert.False(string.IsNullOrEmpty(Compile(sql).Sql));
    }

    [Theory]
    [InlineData("case_01", "SELECT Code FROM Lookup WHERE sqrt(Code) > 3")]
    [InlineData("case_02", "SELECT Code FROM Lookup WHERE CAST(Code AS INTEGER) + 1 > 3")]
    public void Refused_expression_without_policy_or_tenant_predicate_compiles(string item, string sql)
    {
        Assert.NotNull(item);
        Assert.False(string.IsNullOrEmpty(Compile(sql).Sql));
    }

    [Fact]
    public void Correlated_lateral_comparison_of_same_family_compiles()
    {
        Assert.False(string.IsNullOrEmpty(Compile(
            "SELECT o.Id FROM Orders o CROSS JOIN LATERAL (SELECT e.Id FROM Entitlements e WHERE e.OrderId = o.Id) l", TargetSqlDialect.PostgreSql).Sql));
    }

    [Fact]
    public void Correlated_exists_comparison_of_mixed_family_is_refused()
    {
        Assert.Throws<SecurityException>(() => Compile(
            "SELECT Id FROM Orders o WHERE EXISTS (SELECT 1 FROM Entitlements e WHERE e.OrderId = o.Status)"));
    }

    [Theory]
    [InlineData("case_01", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN DATE '2020-13-45' END IS NULL")]
    [InlineData("case_02", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN TIMESTAMP '2020-01-01 25:00:00' END IS NULL")]
    [InlineData("case_03", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN TIME '12:61' END IS NULL")]
    [InlineData("case_04", "SELECT Id FROM Orders WHERE CASE WHEN Id = 42 THEN DATE '2023-02-29' END IS NULL")]
    public void Invalid_typed_literal_is_rejected_when_the_statement_is_built(string item, string sql)
    {
        Assert.NotNull(item);
        Assert.ThrowsAny<Exception>(() => Compile(sql));
    }

    [Theory]
    [InlineData("case_01", "SELECT Id FROM Orders WHERE Id = 1 AND CASE WHEN Id = 2 THEN DATE '2024-02-29' END IS NULL")]
    [InlineData("case_02", "SELECT Id FROM Orders WHERE Id = 1 AND CASE WHEN Id = 2 THEN TIMESTAMP '2024-01-01 23:59:59.123456789' END IS NULL")]
    public void Valid_typed_literal_compiles(string item, string sql)
    {
        Assert.NotNull(item);
        Assert.False(string.IsNullOrEmpty(Compile(sql).Sql));
    }
}
