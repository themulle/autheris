using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// The structured WebSQL view is compiled to a wrapper around the stored statement (<c>SELECT * FROM (stored) AS v WHERE ... ORDER BY ...</c>).
/// These tests pin that the wrapper is governed like any statement: the inner row rule and masks still apply, a masked output column
/// is refused in the outer filter and sort, and the SQL Server LIKE pattern is admitted only for a gateway-owned parameter.
/// </summary>
[Trait("Category", "SqlGate")]
public class StructuredViewWrapperTests
{
    private const string Inner = "SELECT id, region, email FROM orders WHERE amount > __param_min";

    private static ClientListHarness Harness()
    {
        var h = new ClientListHarness();
        h.RowFilters.NoPolicy.Remove(Orders);
        h.RowFilters.Predicates[Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("Region", true)])),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });
        h.Masks.Masks[(Orders, "email")] = new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("m_redact", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_redact"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());
        return h;
    }

    private static string Wrap(string where, string? orderBy = null) =>
        $"SELECT * FROM (\n{Inner}\n) AS v WHERE {where}" + (orderBy is null ? string.Empty : $" ORDER BY {orderBy}");

    public static IEnumerable<object[]> Dialects() => ClientListHarness.DialectData();

    [Theory]
    [MemberData(nameof(Dialects))]
    [Trait("Category", "Security")]
    public void Inner_row_rule_and_mask_still_apply_under_a_view_filter(TargetSqlDialect dialect)
    {
        var h = Harness();

        var compiled = h.Compile(dialect, Wrap("v.region = __param_gw_tf_0"));

        // the row rule value is a policy bind, the filter value is the gateway slot; neither is inlined
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.ClientNamed && p.SourceName == "gw_tf_0");
        Assert.DoesNotContain("EU", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Mask);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    [Trait("Category", "Security")]
    public void A_masked_output_column_is_refused_in_the_view_filter(TargetSqlDialect dialect)
    {
        var h = Harness();

        // counter-check: the unmasked column compiles
        h.Compile(dialect, Wrap("v.id = __param_gw_tf_0"));

        Assert.ThrowsAny<System.Security.SecurityException>(() => h.Compile(dialect, Wrap("v.email = __param_gw_tf_0")));
        Assert.ThrowsAny<System.Security.SecurityException>(() => h.Compile(dialect, Wrap("v.email IS NULL")));
        Assert.ThrowsAny<System.Security.SecurityException>(() => h.Compile(dialect, Wrap("NOT (v.id = __param_gw_tf_0 OR v.email = __param_gw_tf_1)")));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    [Trait("Category", "Security")]
    public void A_masked_output_column_is_refused_in_the_view_sort(TargetSqlDialect dialect)
    {
        var h = Harness();

        h.Compile(dialect, Wrap("v.id = __param_gw_tf_0", "v.id DESC"));

        Assert.ThrowsAny<System.Security.SecurityException>(() => h.Compile(dialect, Wrap("v.id = __param_gw_tf_0", "v.email DESC")));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    [Trait("Category", "Security")]
    public void The_view_cannot_read_a_table_the_inner_statement_does_not_read(TargetSqlDialect dialect)
    {
        var h = Harness();

        // the outer query is reduced to the derived table: a second table in the view text is governed (and here unknown) like any statement
        Assert.ThrowsAny<Exception>(() => h.Compile(dialect, "SELECT * FROM (" + Inner + ") AS v WHERE v.id IN (SELECT secret FROM not_in_catalog)"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Sql_server_like_pattern_is_admitted_only_for_a_gateway_owned_parameter()
    {
        var h = Harness();
        const string sql = "SELECT * FROM (SELECT id, region FROM orders) AS v WHERE v.region LIKE __param_gw_tf_0 ESCAPE '!'";

        Assert.ThrowsAny<System.Security.SecurityException>(() => h.Compile(TargetSqlDialect.SqlServer, sql));

        var request = h.Request(TargetSqlDialect.SqlServer) with { GatewayLikePatternParameters = new HashSet<string> { "gw_tf_0" }.ToFrozenSet() };
        var compiled = h.Engine.Compile(sql.AsMemory(), request, CancellationToken.None);

        Assert.Contains("LIKE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
