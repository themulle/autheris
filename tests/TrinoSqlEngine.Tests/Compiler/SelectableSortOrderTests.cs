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
}
