using System.Security;
using TrinoSqlEngine.Ast.Security;
using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Selectable sort order <c>ORDER BY CASE @sort WHEN 'k' THEN col END, ..., id</c>: the masked-column guard of ORDER BY judges every CASE branch whatever
/// the sort value, and a column the caller cannot see answers like an unknown one.
/// </summary>
[Trait("Category", "SqlGate")]
public class SelectableSortOrderTests
{
    private static readonly TargetSqlDialect[] AllDialects = [.. Dialects, TargetSqlDialect.Databricks];

    public static IEnumerable<object[]> AllDialectData() => AllDialects.Select(d => new object[] { d });

    private readonly ClientListHarness _h = new();

    private void MaskEmail() =>
        _h.Masks.Masks[(PolicyFixtures.Orders, "email")] = new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("m_redact", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_redact"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void Masked_column_inside_order_by_case_is_refused(TargetSqlDialect dialect)
    {
        MaskEmail();
        const string sql = "SELECT id FROM orders ORDER BY CASE __param_sort WHEN 'email' THEN email END, id";

        var forValue = Assert.Throws<MaskedColumnPredicateException>(() => _h.Compile(dialect, sql));
        var forNull = Assert.Throws<MaskedColumnPredicateException>(() => _h.Compile(dialect, sql, nulls: ClientNullShapes.Create(["sort"])));

        Assert.Equal(forValue.Message, forNull.Message);
        Assert.True(forValue.Data.Contains(GovernedSqlErrorCodes.FilterOnMaskedColumn));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void Masked_column_in_case_condition_is_refused(TargetSqlDialect dialect)
    {
        MaskEmail();
        const string sql = "SELECT id FROM orders ORDER BY CASE WHEN email IS NULL THEN 0 ELSE 1 END, id";

        Assert.Throws<MaskedColumnPredicateException>(() => _h.Compile(dialect, sql));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Masked_column_in_nested_case_result_is_refused()
    {
        MaskEmail();
        const string sql = "SELECT id FROM orders ORDER BY CASE __param_sort WHEN 'a' THEN CASE WHEN id > 1 THEN email END END, id";

        Assert.Throws<MaskedColumnPredicateException>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Hidden_column_in_case_answers_like_unknown()
    {
        // a column the catalog does not list (hidden from the caller) is indistinguishable from a column that does not exist
        var unknown = Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql,
            "SELECT id FROM orders ORDER BY CASE __param_sort WHEN 'x' THEN no_such_column END, id"));
        var hidden = Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql,
            "SELECT id FROM orders ORDER BY CASE __param_sort WHEN 'x' THEN secret_hidden_column END, id"));

        Assert.Equal(unknown.GetType(), hidden.GetType());
        Assert.Equal(unknown.Message, hidden.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Unknown_column_in_case_answers_like_plain_unknown()
    {
        // a table without tenant column or row policy: the row-policy guard does not judge it, so the comparison is between the two ORDER BY forms
        var plain = Record.Exception(() => _h.Compile(TargetSqlDialect.SqlServer, "SELECT code FROM lookup ORDER BY no_such_column"));
        var inCase = Record.Exception(() => _h.Compile(TargetSqlDialect.SqlServer,
            "SELECT code FROM lookup ORDER BY CASE __param_sort WHEN 'x' THEN no_such_column END, code"));

        Assert.Equal(plain?.GetType(), inCase?.GetType());
        Assert.Equal(plain?.Message, inCase?.Message);
        Assert.Equal((plain as GovernedSqlException)?.Code, (inCase as GovernedSqlException)?.Code);
    }

    private const string SortSql = "SELECT id FROM orders ORDER BY CASE __param_sort WHEN 'region' THEN region END, " +
        "CASE __param_sort WHEN 'status' THEN status END, id";

    private static List<BoundParameter> SortSlots(CompiledSql compiled) =>
        compiled.Parameters.Where(p => p.Origin == ParameterOrigin.ClientNamed && string.Equals(p.SourceName, "sort", StringComparison.OrdinalIgnoreCase)).ToList();

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Case_per_key_order_by_compiles_and_emits_binds(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, SortSql);

        Assert.Single(SortSlots(compiled));
        Assert.Contains("CASE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__param_", compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Sort_pattern_with_page_offset_compiles(TargetSqlDialect dialect)
    {
        var compiled = _h.Engine.Compile(SortSql.AsMemory(), _h.Request(dialect) with
        {
            BindPageOffset = true,
            EnforcedMaxRows = 100,
            PageOffset = 0
        }, CancellationToken.None);

        Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.PageOffset);
        Assert.Single(SortSlots(compiled));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Sort_pattern_compiles_under_row_policy(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, SortSql);

        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.All(SortSlots(compiled), s => Assert.Equal(BindFamily.Text, s.RequiredFamily));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    public void Null_sort_value_orders_by_tie_breaker_only(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, SortSql, nulls: ClientNullShapes.Create(["sort"]));

        // a NULL sort value matches no WHEN: every CASE key is NULL and the unique tie-breaker alone orders the rows
        Assert.Contains("CASE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    // ---- row policy: the root ORDER BY resolves columns in the scope of its single query block ----

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderByCase_ColumnFamilyResolvedInBodyScope_UnderRowPolicy(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE __param_sort WHEN 'region' THEN Region WHEN 'status' THEN Status END, Id");

        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderByCase_MixedFamilies_StillRefused(TargetSqlDialect dialect)
    {
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE __param_sort WHEN 'region' THEN Region WHEN 'amount' THEN Amount END, Id"));
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE WHEN Id > 1 THEN Region ELSE Amount END, Id"));
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE WHEN Region > 1 THEN Id END"));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderBy_SetOperationBody_CaseColumn_StillRefused(TargetSqlDialect dialect)
    {
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id, Region FROM Orders UNION ALL SELECT Id, Region FROM Orders ORDER BY CASE WHEN Region = 'a' THEN Region END"));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderByCase_OutputAliasFamily_IsProjectionFamily(TargetSqlDialect dialect)
    {
        // an alias takes the family of the projection it names ...
        _h.Compile(dialect, "SELECT Region AS k, Id FROM Orders ORDER BY CASE __param_sort WHEN 'k' THEN k WHEN 'r' THEN Region END, Id");
        // ... never the family of a column that happens to share a name elsewhere: Amount is numeric, Region is text
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Amount AS k, Id FROM Orders ORDER BY CASE WHEN Id > 1 THEN k ELSE Region END, Id"));
        // repeated output names stay unknown
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Region AS k, Region AS k FROM Orders ORDER BY CASE WHEN Id > 1 THEN k ELSE Region END"));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderByCase_UnresolvableColumn_StillRefused(TargetSqlDialect dialect)
    {
        Assert.ThrowsAny<Exception>(() => _h.Compile(dialect, "SELECT Id FROM Orders ORDER BY CASE WHEN no_such = 'a' THEN Region ELSE Status END"));
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE WHEN nope.Region = 'a' THEN nope.Region ELSE Status END"));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderByCase_MaskedColumn_UnderRowPolicy_IsFilterOnMaskedColumn(TargetSqlDialect dialect)
    {
        MaskEmail();

        var ex = Assert.Throws<MaskedColumnPredicateException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders ORDER BY CASE __param_sort WHEN 'email' THEN Email WHEN 'region' THEN Region END, Id"));

        Assert.True(ex.Data.Contains(GovernedSqlErrorCodes.FilterOnMaskedColumn));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderBy_OuterScopeOrCorrelatedRowPolicyColumn_StillRefused(TargetSqlDialect dialect)
    {
        // a column of a source that is not in the body scope
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders AS o ORDER BY CASE WHEN x.Region = 'a' THEN x.Region ELSE o.Status END"));
        // the ORDER BY of a subquery or CTE is not the root ORDER BY: it keeps the unresolved families
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id FROM Orders WHERE Id IN (SELECT OrderId FROM Entitlements ORDER BY CASE WHEN Id > 1 THEN OrderId END)"));
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "WITH c AS (SELECT Id, Region FROM Orders ORDER BY CASE WHEN zz > 1 THEN Region END) SELECT Id FROM c"));
    }

    [Theory]
    [MemberData(nameof(AllDialectData))]
    [Trait("Category", "Security")]
    public void OrderBy_WindowAndAggregateOrderBy_Unchanged(TargetSqlDialect dialect)
    {
        // a window ORDER BY inside a projection and an aggregate ORDER BY resolve as before (the block scope is not re-entered for them)
        Assert.Throws<RowPolicyExpressionRejectedException>(() => _h.Compile(dialect,
            "SELECT Id, row_number() OVER (ORDER BY CASE WHEN Id > 1 THEN Region ELSE Amount END) AS rn FROM Orders"));
    }
}
