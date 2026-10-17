using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>REQ-SQL-128 WP-1: expansion of a client list in an IN / NOT IN list on the AST (PLAN-SQL-128 A-1, A-2).</summary>
public class ClientListExpansionTests
{
    private readonly ClientListHarness _h = new();

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Expander_ReplacesSoleListItemWithIndexedReferences(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids)", Shapes(("ids", 3)));

        var slots = ListSlots(compiled, "ids");
        Assert.Equal(3, slots.Count);
        Assert.Equal([0, 1, 2], slots.Select(s => s.ListIndex!.Value));
        Assert.All(slots, s => Assert.Equal(3, s.ListCount));
        Assert.Contains($"IN ({string.Join(", ", slots.Select(s => s.Marker))})", compiled.Sql);
        Assert.DoesNotContain("__param_", compiled.Sql);
        Assert.Equal(compiled.Parameters.Length, compiled.Parameters.Select(p => p.Ordinal).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Expander_EmptyList_IsConstantFalse_AndNotIn_IsConstantTrue(TargetSqlDialect dialect)
    {
        var inList = _h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids)", Shapes(("ids", 0)));
        var notIn = _h.Compile(dialect, "SELECT id FROM orders WHERE id NOT IN (__param_ids)", Shapes(("ids", 0)));

        Assert.Contains("1 = 0", inList.Sql);
        Assert.Contains("1 = 1", notIn.Sql);
        Assert.Empty(ListSlots(inList, "ids"));
        Assert.Empty(ListSlots(notIn, "ids"));
        Assert.DoesNotContain(inList.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.DoesNotContain(notIn.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE id = __param_l")]
    [InlineData("SELECT id FROM orders WHERE UPPER(status) = UPPER(__param_l)")]
    [InlineData("SELECT id FROM orders ORDER BY __param_l")]
    [InlineData("SELECT id FROM orders WHERE id = CASE WHEN id = 1 THEN __param_l ELSE 0 END")]
    [InlineData("SELECT __param_l AS x FROM orders")]
    [InlineData("SELECT id FROM orders WHERE __param_l IN (1, 2)")]
    [InlineData("SELECT id FROM orders WHERE id IN (__param_l, 1)")]
    [InlineData("SELECT id FROM orders WHERE id IN (1, __param_l)")]
    [InlineData("SELECT id FROM orders WHERE id IN (__param_l, __param_m)")]
    [InlineData("SELECT id FROM orders WHERE id IN (__param_l, __param_l)")]
    [InlineData("SELECT id FROM orders WHERE id IN (CAST(__param_l AS int))")]
    [InlineData("SELECT id FROM orders WHERE id BETWEEN __param_l AND 3")]
    [InlineData("SELECT id FROM orders WHERE id > __param_l")]
    [InlineData("SELECT id FROM orders WHERE status LIKE __param_l")]
    [InlineData("SELECT id FROM (VALUES (1), (2)) AS v(id) WHERE id IN (SELECT id FROM orders WHERE id = __param_l)")]
    [Trait("Category", "Security")]
    public void Expander_ListOutsideInList_IsRejected(string sql)
    {
        var ex = Assert.Throws<ClientListException>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, Shapes(("l", 3), ("m", 2))));
        Assert.Equal(GovernedSqlErrorCodes.ListParamPosition, ex.Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Expander_PositionRule_IsNotAppliedToScalarParameters_AndNotInOutsidePositions()
    {
        // A name that is not a list stays scalar in every position.
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = __param_s AND status IN (__param_t)", Shapes(("l", 3)));
        Assert.Equal(2, compiled.Parameters.Count(p => p.Origin == ParameterOrigin.ClientNamed));
        Assert.All(compiled.Parameters.Where(p => p.Origin == ParameterOrigin.ClientNamed), p => Assert.Null(p.ListIndex));
    }

    [Fact]
    public void Expander_ScalarParameterInIn_StaysScalar()
    {
        var compiled = _h.Compile(TargetSqlDialect.SqlServer, "SELECT id FROM orders WHERE id IN (__param_one)");
        var client = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.Null(client.ListIndex);
        Assert.Null(client.ListCount);
    }

    [Fact]
    public void ListNames_AreMatchedCaseInsensitively()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id IN (__param_Ids)", Shapes(("IDS", 2)));
        Assert.Equal(2, ListSlots(compiled, "ids").Count);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void SameListUsedTwice_SharesBinds(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids) OR amount IN (__param_ids)", Shapes(("ids", 4)));
        Assert.Equal(4, ListSlots(compiled, "ids").Count);
        var markers = ListSlots(compiled, "ids").Select(s => s.Marker).ToList();
        string list = $"IN ({string.Join(", ", markers)})";
        Assert.Equal(2, compiled.Sql.Split(list).Length - 1);
    }

    [Fact]
    public void DifferentSpellingsOfOneList_ProduceIndependentSlotsOfTheSameList()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id IN (__param_Ids) OR amount IN (__param_ids)", Shapes(("ids", 2)));
        Assert.Equal(4, compiled.Parameters.Count(p => p.Origin == ParameterOrigin.ClientNamed));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Expander_ParenthesizedItem_IsNeverUnwrapped()
    {
        // The sole-item rule is on the exact node type (SEC-128-10 d), independent of what the AST builder does with parentheses.
        var list = new InListExpression(
            new LiteralExpression(1L, LiteralType.Integer),
            [new ParenthesizedExpression(new ParameterReference("l", null, true))],
            false);
        var caps = TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);
        var expander = new ClientListExpander(Shapes(("l", 3)), caps);

        var ex = Assert.Throws<ClientListException>(() => expander.Visit(list));
        Assert.Equal(GovernedSqlErrorCodes.ListParamPosition, ex.Code);
    }

    [Fact]
    public void StructuralEquals_DistinguishesListIndexes()
    {
        var statement = CompilerTestHelpers.Build("SELECT id FROM orders WHERE __param_a = 1 OR __param_a = 1");
        var indexed = (SqlStatement)new IndexingRewriter().Visit(statement);
        var simplified = (SelectStatement)new AstSimplificationVisitor().Visit(indexed);
        var where = ((QuerySpecification)simplified.Body).Where;
        var or = Assert.IsType<BinaryExpression>(where);
        Assert.Equal(BinaryOperator.Or, or.Operator);

        var plain = (SelectStatement)new AstSimplificationVisitor().Visit(statement);
        Assert.False(((QuerySpecification)plain.Body).Where is BinaryExpression { Operator: BinaryOperator.Or });
    }

    private sealed class IndexingRewriter : SqlAstRewriter
    {
        private int _next;

        public override SqlNode VisitParameterReference(ParameterReference node) => node with { ListIndex = _next++, ListCount = 2 };
    }
}
