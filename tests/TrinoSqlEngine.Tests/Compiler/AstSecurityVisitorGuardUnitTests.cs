using System.Reflection;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate unit tests (M-12) of the private guards of <see cref="AstSecurityVisitor"/> that the typed compile path reaches only
/// through a simplified tree (tautology detection, column reference search) or not at all in a given configuration (masked column
/// traversal over every node kind, table source collection, whole-row detection). The tests call the guards directly on hand-built
/// trees, so every node kind and every comparison operator is exercised.
/// </summary>
[Trait("Category", "SqlGate")]
public class AstSecurityVisitorGuardUnitTests
{
    private sealed class Lookup(Func<string, string, bool> has) : IMaskedColumnLookup
    {
        public bool HasMask(string normalizedTableName, string columnName) => has(normalizedTableName, columnName);
    }

    private static AstSecurityVisitor Visitor(Func<string, string, bool>? has = null, Action<AstSecurityVisitorOptionsBuilder>? tweak = null)
    {
        var b = new AstSecurityVisitorOptionsBuilder { Lookup = new Lookup(has ?? ((t, c) => c == "ssn")) };
        tweak?.Invoke(b);
        var typed = new TypedPolicyContext(Catalog(), new DictPolicyProvider(), new DictMaskProvider(),
            new TenantBinding("t", "a", SqlParameterType.String), DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer));
        return new AstSecurityVisitor(b.Build(), typed);
    }

    private sealed class AstSecurityVisitorOptionsBuilder
    {
        public IMaskedColumnLookup Lookup { get; set; } = null!;
        public HashSet<string> MaskedTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<string, IReadOnlyList<string>?> Columns { get; set; } = _ => null;
        public bool InDml { get; set; } = true;
        public bool InPredicates { get; set; } = true;
        public bool WholeRow { get; set; } = true;

        public AstSecurityVisitorOptions Build() => new()
        {
            MaskedColumns = Lookup,
            TableColumns = Columns,
            TablesWithMaskedColumns = MaskedTables,
            RejectMaskedColumnsInDml = InDml,
            RejectMaskedColumnsInPredicates = InPredicates,
            RejectWholeRowReferencesInDml = WholeRow
        };
    }

    private static T Call<T>(object? target, string name, params object?[] args)
    {
        var method = typeof(AstSecurityVisitor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new MissingMethodException(name);
        try { return (T)method.Invoke(target, args)!; }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private static ColumnReference Col(string name) => new(new SqlQualifiedName(name));
    private static ColumnReference Col(string table, string name) => new(new SqlQualifiedName(table, name));
    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);
    private static LiteralExpression Str(string v) => new(v, LiteralType.String);
    private static LiteralExpression Bool(bool v) => new(v, LiteralType.Boolean);
    private static LiteralExpression Dec(decimal v) => new(v, LiteralType.Decimal);
    private static BinaryExpression Bin(Expression l, BinaryOperator op, Expression r) => new(l, op, r);
    private static QuerySpecification Spec(Expression? where = null, TableSource? from = null, GroupByClause? group = null, Expression? having = null, params SelectItem[] items) =>
        new(false, items.Length == 0 ? new SelectItem[] { new ColumnSelectItem(Int(1), null) } : items, from, where, group, having);
    private static SelectStatement Select(QueryBody body, OrderByClause? order = null, PaginationClause? page = null, WithClause? with = null) => new(with, body, order, page);

    // ---- tautology and column detection ----

    private static bool True(Expression e) => Call<bool>(null, "IsTriviallyTrue", e);
    private static bool False(Expression e) => Call<bool>(null, "IsTriviallyFalse", e);
    private static bool HasColumn(Expression? e) => Call<bool>(null, "ContainsColumnReference", e);

    [Fact]
    public void IsTriviallyTrue_ComparesNumericLiterals_PerOperator()
    {
        foreach (var (op, l, r, expected) in new (BinaryOperator, long, long, bool)[]
                 {
                     (BinaryOperator.Equal, 1, 1, true), (BinaryOperator.Equal, 1, 2, false),
                     (BinaryOperator.NotEqual, 1, 2, true), (BinaryOperator.NotEqual, 1, 1, false),
                     (BinaryOperator.LessThan, 1, 2, true), (BinaryOperator.LessThan, 2, 2, false), (BinaryOperator.LessThan, 3, 2, false),
                     (BinaryOperator.LessThanOrEqual, 1, 2, true), (BinaryOperator.LessThanOrEqual, 2, 2, true), (BinaryOperator.LessThanOrEqual, 3, 2, false),
                     (BinaryOperator.GreaterThan, 3, 2, true), (BinaryOperator.GreaterThan, 2, 2, false), (BinaryOperator.GreaterThan, 1, 2, false),
                     (BinaryOperator.GreaterThanOrEqual, 3, 2, true), (BinaryOperator.GreaterThanOrEqual, 2, 2, true), (BinaryOperator.GreaterThanOrEqual, 1, 2, false)
                 })
        {
            Assert.Equal(expected, True(Bin(Int(l), op, Int(r))));
        }
    }

    [Fact]
    public void IsTriviallyTrue_ComparesTextLiteralsAndMixedKinds()
    {
        Assert.True(True(Bin(Str("a"), BinaryOperator.Equal, Str("a"))));
        Assert.False(True(Bin(Str("a"), BinaryOperator.Equal, Str("b"))));
        Assert.True(True(Bin(Str("a"), BinaryOperator.NotEqual, Str("b"))));
        Assert.False(True(Bin(Str("a"), BinaryOperator.NotEqual, Str("a"))));
        Assert.True(True(Bin(Str("a"), BinaryOperator.LessThanOrEqual, Str("a"))));
        Assert.False(True(Bin(Str("a"), BinaryOperator.LessThanOrEqual, Str("b"))));
        Assert.True(True(Bin(Str("a"), BinaryOperator.GreaterThanOrEqual, Str("a"))));
        Assert.False(True(Bin(Str("a"), BinaryOperator.GreaterThanOrEqual, Str("b"))));
        Assert.False(True(Bin(Str("a"), BinaryOperator.LessThan, Str("b"))));
        Assert.False(True(Bin(Str("b"), BinaryOperator.GreaterThan, Str("a"))));
        Assert.True(True(Bin(Int(1), BinaryOperator.Equal, Dec(1.0m))));
        Assert.True(True(Bin(Dec(1.5m), BinaryOperator.GreaterThan, Int(1))));
        Assert.False(True(Bin(new LiteralExpression(null, LiteralType.Null), BinaryOperator.Equal, Int(1))));
        Assert.False(True(Bin(Int(1), BinaryOperator.Equal, new LiteralExpression(null, LiteralType.Null))));
        Assert.False(True(Bin(new LiteralExpression(null, LiteralType.Null), BinaryOperator.NotEqual, new LiteralExpression(null, LiteralType.Null))));
        Assert.False(True(Bin(Int(1), BinaryOperator.Add, Int(1))));
    }

    [Fact]
    public void IsTriviallyTrue_HandlesColumnsBooleansAndStructure()
    {
        Assert.True(True(Bool(true)));
        Assert.False(True(Bool(false)));
        Assert.False(True(Int(1)));
        Assert.False(True(new LiteralExpression("true", LiteralType.String)));
        Assert.False(Call<bool>(null, "IsTriviallyTrue", new object?[] { null }));
        Assert.True(True(new ParenthesizedExpression(Bool(true))));
        Assert.True(True(Bin(Col("a"), BinaryOperator.Equal, Col("A"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.Equal, Col("b"))));
        Assert.True(True(Bin(Col("a"), BinaryOperator.LessThanOrEqual, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.LessThanOrEqual, Col("b"))));
        Assert.True(True(Bin(Col("a"), BinaryOperator.GreaterThanOrEqual, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.GreaterThanOrEqual, Col("b"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.LessThan, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.GreaterThan, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.NotEqual, Col("a"))));
        Assert.True(True(Bin(Col("a"), BinaryOperator.Or, Bool(true))));
        Assert.True(True(Bin(Bool(true), BinaryOperator.Or, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.Or, Col("b"))));
        Assert.True(True(Bin(Bool(true), BinaryOperator.And, Bool(true))));
        Assert.False(True(Bin(Bool(true), BinaryOperator.And, Col("a"))));
        Assert.False(True(Bin(Col("a"), BinaryOperator.And, Bool(true))));
        Assert.True(True(Bin(new UnaryExpression(UnaryOperator.IsNull, Col("a")), BinaryOperator.Or, new UnaryExpression(UnaryOperator.IsNotNull, Col("a")))));
        Assert.True(True(Bin(new UnaryExpression(UnaryOperator.IsNotNull, Col("a")), BinaryOperator.Or, new UnaryExpression(UnaryOperator.IsNull, Col("A")))));
        Assert.False(True(Bin(new UnaryExpression(UnaryOperator.IsNull, Col("a")), BinaryOperator.Or, new UnaryExpression(UnaryOperator.IsNotNull, Col("b")))));
        Assert.False(True(Bin(new UnaryExpression(UnaryOperator.IsNull, Col("a")), BinaryOperator.Or, new UnaryExpression(UnaryOperator.IsNull, Col("a")))));
        Assert.False(True(Bin(new UnaryExpression(UnaryOperator.IsNotNull, Col("a")), BinaryOperator.Or, new UnaryExpression(UnaryOperator.IsNotNull, Col("a")))));
        Assert.True(True(new BetweenExpression(Col("a"), Col("a"), Col("a"), false)));
        Assert.False(True(new BetweenExpression(Col("a"), Col("a"), Col("b"), false)));
        Assert.False(True(new BetweenExpression(Col("a"), Col("b"), Col("a"), false)));
        Assert.False(True(new BetweenExpression(Int(1), Col("a"), Col("a"), false)));
        Assert.True(True(new InListExpression(Col("a"), new Expression[] { Col("b"), Col("a") }, false)));
        Assert.False(True(new InListExpression(Col("a"), new Expression[] { Col("b") }, false)));
        Assert.False(True(new InListExpression(Int(1), new Expression[] { Col("a") }, false)));
        Assert.True(True(new UnaryExpression(UnaryOperator.IsNotNull, Int(1))));
        Assert.False(True(new UnaryExpression(UnaryOperator.IsNotNull, new LiteralExpression(null, LiteralType.Null))));
        Assert.False(True(new UnaryExpression(UnaryOperator.IsNotNull, Col("a"))));
        Assert.False(True(new UnaryExpression(UnaryOperator.Negate, Bool(true))));
    }

    [Fact]
    public void IsTriviallyFalse_AndNot_FoldConstants()
    {
        Assert.True(False(Bool(false)));
        Assert.False(False(Bool(true)));
        Assert.False(False(Int(0)));
        Assert.True(False(new ParenthesizedExpression(Bool(false))));
        Assert.True(False(new UnaryExpression(UnaryOperator.Not, Bool(true))));
        Assert.False(False(new UnaryExpression(UnaryOperator.Not, Bool(false))));
        Assert.True(False(Bin(Bool(false), BinaryOperator.And, Col("a"))));
        Assert.True(False(Bin(Col("a"), BinaryOperator.And, Bool(false))));
        Assert.False(False(Bin(Col("a"), BinaryOperator.And, Col("b"))));
        Assert.True(False(Bin(Bool(false), BinaryOperator.Or, Bool(false))));
        Assert.False(False(Bin(Bool(false), BinaryOperator.Or, Col("a"))));
        Assert.False(False(Bin(Col("a"), BinaryOperator.Or, Bool(false))));
        foreach (var (op, l, r, expected) in new (BinaryOperator, long, long, bool)[]
                 {
                     (BinaryOperator.Equal, 1, 2, true), (BinaryOperator.Equal, 1, 1, false),
                     (BinaryOperator.NotEqual, 1, 1, true), (BinaryOperator.NotEqual, 1, 2, false),
                     (BinaryOperator.LessThan, 2, 1, true), (BinaryOperator.LessThan, 1, 1, true), (BinaryOperator.LessThan, 1, 2, false),
                     (BinaryOperator.LessThanOrEqual, 2, 1, true), (BinaryOperator.LessThanOrEqual, 1, 1, false), (BinaryOperator.LessThanOrEqual, 1, 2, false),
                     (BinaryOperator.GreaterThan, 1, 2, true), (BinaryOperator.GreaterThan, 1, 1, true), (BinaryOperator.GreaterThan, 2, 1, false),
                     (BinaryOperator.GreaterThanOrEqual, 1, 2, true), (BinaryOperator.GreaterThanOrEqual, 1, 1, false), (BinaryOperator.GreaterThanOrEqual, 2, 1, false)
                 })
        {
            Assert.Equal(expected, False(Bin(Int(l), op, Int(r))));
        }

        Assert.False(False(Bin(Int(1), BinaryOperator.Add, Int(1))));
        Assert.False(False(Bin(new LiteralExpression(null, LiteralType.Null), BinaryOperator.Equal, Int(1))));
        Assert.False(Call<bool>(null, "IsTriviallyFalse", new object?[] { null }));
    }

    [Fact]
    public void NotOfAConstantFalse_IsATautology_AndNotOfATrueIsNot()
    {
        Assert.True(True(new UnaryExpression(UnaryOperator.Not, Bool(false))));
        Assert.True(True(new UnaryExpression(UnaryOperator.Not, Bin(Int(1), BinaryOperator.Equal, Int(2)))));
        Assert.False(True(new UnaryExpression(UnaryOperator.Not, Bool(true))));
        Assert.False(True(new UnaryExpression(UnaryOperator.Not, Col("a"))));
    }

    [Fact]
    public void ContainsColumnReference_LooksIntoEveryExpressionKind()
    {
        var c = Col("a");
        var n = Int(1);
        var sub = Select(Spec(from: new NamedTableSource(new SqlQualifiedName("t"), null), items: new ColumnSelectItem(c, null)));
        var lit = Select(Spec());
        Assert.False(HasColumn(null));
        Assert.True(HasColumn(c));
        Assert.False(HasColumn(n));
        Assert.True(HasColumn(Bin(n, BinaryOperator.Add, c)));
        Assert.True(HasColumn(Bin(c, BinaryOperator.Add, n)));
        Assert.True(HasColumn(new UnaryExpression(UnaryOperator.Negate, c)));
        Assert.True(HasColumn(new LikeExpression(c, n)));
        Assert.True(HasColumn(new LikeExpression(n, c)));
        Assert.True(HasColumn(new LikeExpression(n, n, c)));
        Assert.False(HasColumn(new LikeExpression(n, n, n)));
        Assert.False(HasColumn(new LikeExpression(n, n)));
        Assert.True(HasColumn(new InListExpression(n, new Expression[] { n, c }, false)));
        Assert.True(HasColumn(new InListExpression(c, new Expression[] { n }, false)));
        Assert.False(HasColumn(new InListExpression(n, new Expression[] { n, n }, false)));
        Assert.True(HasColumn(new InSubqueryExpression(c, lit, false)));
        Assert.True(HasColumn(new InSubqueryExpression(n, sub, false)));
        Assert.False(HasColumn(new InSubqueryExpression(n, lit, false)));
        Assert.True(HasColumn(new ExistsExpression(sub)));
        Assert.False(HasColumn(new ExistsExpression(lit)));
        Assert.True(HasColumn(new ScalarSubqueryExpression(sub)));
        Assert.True(HasColumn(new BetweenExpression(c, n, n, false)));
        Assert.True(HasColumn(new BetweenExpression(n, c, n, false)));
        Assert.True(HasColumn(new BetweenExpression(n, n, c, false)));
        Assert.False(HasColumn(new BetweenExpression(n, n, n, false)));
        Assert.True(HasColumn(new CaseExpression(c, new[] { new WhenClause(n, n) }, null)));
        Assert.True(HasColumn(new CaseExpression(null, new[] { new WhenClause(c, n) }, null)));
        Assert.True(HasColumn(new CaseExpression(null, new[] { new WhenClause(n, c) }, null)));
        Assert.True(HasColumn(new CaseExpression(null, new[] { new WhenClause(n, n), new WhenClause(n, c) }, null)));
        Assert.True(HasColumn(new CaseExpression(null, new[] { new WhenClause(n, n) }, c)));
        Assert.False(HasColumn(new CaseExpression(null, new[] { new WhenClause(n, n) }, null)));
        Assert.False(HasColumn(new CaseExpression(n, new[] { new WhenClause(n, n) }, n)));
        Assert.True(HasColumn(new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n, c })));
        Assert.False(HasColumn(new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n, n })));
        Assert.True(HasColumn(new SubstringExpression(c, n, n)));
        Assert.True(HasColumn(new SubstringExpression(n, c, n)));
        Assert.True(HasColumn(new SubstringExpression(n, n, c)));
        Assert.False(HasColumn(new SubstringExpression(n, n, n)));
        Assert.False(HasColumn(new SubstringExpression(n, n, null)));
        Assert.True(HasColumn(new TrimExpression(TrimSpecification.Both, c, n)));
        Assert.True(HasColumn(new TrimExpression(TrimSpecification.Both, n, c)));
        Assert.False(HasColumn(new TrimExpression(TrimSpecification.Both, n, n)));
        Assert.False(HasColumn(new TrimExpression(TrimSpecification.Both, n, null)));
        Assert.True(HasColumn(new PositionExpression(c, n)));
        Assert.True(HasColumn(new PositionExpression(n, c)));
        Assert.False(HasColumn(new PositionExpression(n, n)));
        Assert.True(HasColumn(new CastExpression(c, "INT", false, false)));
        Assert.True(HasColumn(new DateFunctionExpression(DateFunctionKind.Trunc, DateUnit.Day, 0, c)));
        Assert.True(HasColumn(new ExtractExpression("year", c)));
        Assert.True(HasColumn(new IsDistinctFromExpression(c, n, false)));
        Assert.True(HasColumn(new IsDistinctFromExpression(n, c, false)));
        Assert.False(HasColumn(new IsDistinctFromExpression(n, n, false)));
        Assert.True(HasColumn(new QuantifiedComparisonExpression(c, BinaryOperator.Equal, ComparisonQuantifier.Any, lit)));
        Assert.True(HasColumn(new QuantifiedComparisonExpression(n, BinaryOperator.Equal, ComparisonQuantifier.Any, sub)));
        Assert.False(HasColumn(new QuantifiedComparisonExpression(n, BinaryOperator.Equal, ComparisonQuantifier.Any, lit)));
    }
    // ---- masked column traversal ----

    private static void AssertMasked(SqlNode scope, string table = "dbo.Orders", string clause = "WHERE", AstSecurityVisitor? visitor = null)
    {
        var ex = Assert.Throws<SecurityException>(() => Call<object?>(visitor ?? Visitor(), "CheckNoMaskedColumnReferences", table, null, scope, clause));
        Assert.Equal($"Masked column 'ssn' of table '{table}' must not be referenced in {clause}.", ex.Message);
    }

    private static void AssertClean(SqlNode scope, AstSecurityVisitor? visitor = null) =>
        Call<object?>(visitor ?? Visitor(), "CheckNoMaskedColumnReferences", "dbo.Orders", null, scope, "WHERE");

    [Fact]
    public void TheMaskedColumnTraversal_ReachesEveryChildOfEveryNodeKind()
    {
        var s = Col("ssn");
        var n = Int(1);
        var from = new NamedTableSource(new SqlQualifiedName("t"), null);
        var inner = Select(Spec(from: from, items: new ColumnSelectItem(s, null)));
        var clean = Select(Spec(from: from));
        var order = new OrderByClause(new[] { new OrderByElement(s) });
        var nodes = new SqlNode[]
        {
            new ParenthesizedExpression(s),
            new UnaryExpression(UnaryOperator.Not, s),
            new CastExpression(s, "INT", false, false),
            new LikeExpression(s, n), new LikeExpression(n, s), new LikeExpression(n, n, s),
            new IsDistinctFromExpression(s, n, false), new IsDistinctFromExpression(n, s, false),
            new BetweenExpression(s, n, n, false), new BetweenExpression(n, s, n, false), new BetweenExpression(n, n, s, false),
            new InListExpression(s, new Expression[] { n }, false), new InListExpression(n, new Expression[] { n, s }, false),
            new InSubqueryExpression(s, clean, false), new InSubqueryExpression(n, inner, false),
            new ScalarSubqueryExpression(inner), new ExistsExpression(inner),
            new QuantifiedComparisonExpression(s, BinaryOperator.Equal, ComparisonQuantifier.Any, clean),
            new QuantifiedComparisonExpression(n, BinaryOperator.Equal, ComparisonQuantifier.Any, inner),
            new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n, s }),
            new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n }, Filter: s),
            new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n }, OrderWithin: order),
            new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n }, Window: new WindowSpecification(new Expression[] { s }, null)),
            new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n }, Window: new WindowSpecification(null, order)),
            new CaseExpression(s, new[] { new WhenClause(n, n) }, null), new CaseExpression(null, new[] { new WhenClause(s, n) }, null),
            new CaseExpression(null, new[] { new WhenClause(n, s) }, null), new CaseExpression(null, new[] { new WhenClause(n, n) }, s),
            new RowValueExpression(new Expression[] { n, s }), new ArrayConstructorExpression(new Expression[] { n, s }),
            new SubscriptExpression(s, n), new SubscriptExpression(n, s), new ExtractExpression("year", s),
            new SubstringExpression(s, n, n), new SubstringExpression(n, s, n), new SubstringExpression(n, n, s),
            new TrimExpression(TrimSpecification.Both, s, n), new TrimExpression(TrimSpecification.Both, n, s),
            new DateFunctionExpression(DateFunctionKind.Trunc, DateUnit.Day, 0, s),
            new PositionExpression(s, n), new PositionExpression(n, s),
            Select(Spec(from: from), page: new PaginationClause(s, null)), Select(Spec(from: from), page: new PaginationClause(null, s)),
            Select(Spec(from: from), order: order),
            Select(Spec(from: from), with: new WithClause(false, new[] { new CommonTableExpression(new SqlIdentifier("c"), null, inner) })),
            Select(Spec(from: from, group: new GroupByClause(new Expression[] { s }))),
            Select(Spec(from: from, group: new GroupByClause(Array.Empty<Expression>(), new[] { new GroupingElement(GroupingElementKind.GroupingSets, new IReadOnlyList<Expression>[] { new Expression[] { s } }) }))),
            Select(Spec(from: from, having: s)),
            Select(Spec(where: s, from: from)),
            Select(Spec(from: new SubqueryTableSource(inner, new SqlIdentifier("x")))),
            Select(Spec(from: new LateralTableSource(inner, new SqlIdentifier("x")))),
            Select(Spec(from: new JoinedTableSource(new SubqueryTableSource(inner, new SqlIdentifier("a")), JoinType.Inner, from, null))),
            Select(Spec(from: new JoinedTableSource(from, JoinType.Inner, new SubqueryTableSource(inner, new SqlIdentifier("a")), null))),
            Select(Spec(from: new JoinedTableSource(from, JoinType.Inner, from, new OnJoinCondition(s)))),
            Select(new SetOperationQuery(Spec(from: from, items: new ColumnSelectItem(s, null)), SetOperator.Union, false, Spec(from: from))),
            Select(new SetOperationQuery(Spec(from: from), SetOperator.Union, false, Spec(from: from, items: new ColumnSelectItem(s, null)))),
            Select(new ValuesQueryBody(new[] { new RowValueExpression(new Expression[] { s }) })),
            new OrderByClause(new[] { new OrderByElement(Int(1)), new OrderByElement(s) })
        };
        foreach (var node in nodes) AssertMasked(node);
        AssertClean(Select(Spec(from: from)));
        AssertClean(new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { n }));
    }

    [Fact]
    public void AColumnIsMasked_ByItsTable_ItsQualifier_OrAnyMaskedTable()
    {
        var byTable = Visitor((t, c) => t == "dbo.Orders" && c == "k");
        Assert.Throws<SecurityException>(() => Call<object?>(byTable, "CheckNoMaskedColumnReferences", "dbo.Orders", null, Col("k"), "WHERE"));
        Assert.Throws<SecurityException>(() => Call<object?>(byTable, "CheckNoMaskedColumnReferences", "dbo.Orders", null, Col("dbo.Orders".Split('.')[1], "k"), "WHERE"));
        AssertClean(Col("other"), byTable);

        var byQualifier = Visitor((t, c) => t == "q" && c == "k");
        Assert.Throws<SecurityException>(() => Call<object?>(byQualifier, "CheckNoMaskedColumnReferences", "dbo.Orders", null, Col("q", "k"), "WHERE"));
        AssertClean(Col("k"), byQualifier);
        AssertClean(Col("z", "k"), byQualifier);

        var byAnyTable = Visitor((t, c) => t == "zzz" && c == "k", b => b.MaskedTables.Add("zzz"));
        Assert.Throws<SecurityException>(() => Call<object?>(byAnyTable, "CheckNoMaskedColumnReferences", "dbo.Orders", null, Col("k"), "WHERE"));
        AssertClean(Col("other"), byAnyTable);
        // a USING column is asked per table as well
        var usingNode = new UsingJoinCondition(new[] { new SqlIdentifier("k") });
        Assert.Throws<SecurityException>(() => Call<object?>(byAnyTable, "CheckNoMaskedColumnReferences", "dbo.Orders", null, usingNode, "JOIN condition"));
        Assert.Throws<SecurityException>(() => Call<object?>(byTable, "CheckNoMaskedColumnReferences", "dbo.Orders", null, usingNode, "JOIN condition"));
        AssertClean(new UsingJoinCondition(new[] { new SqlIdentifier("other") }), byAnyTable);
        AssertClean(new UsingJoinCondition(new[] { new SqlIdentifier("a"), new SqlIdentifier("other") }), byTable);
        var noTables = Visitor((t, c) => false);
        AssertClean(Col("k"), noTables);
    }

    [Fact]
    public void WholeRowReferences_AreRejected_ForTheTableItsSimpleNameAndItsAlias()
    {
        Action<AstSecurityVisitorOptionsBuilder> masked = b => b.MaskedTables.Add("dbo.Orders");
        var v = Visitor((t, c) => false, masked);
        string Whole(string table, string? alias, SqlNode scope) =>
            Assert.Throws<SecurityException>(() => Call<object?>(v, "CheckNoMaskedColumnReferences", table, alias, scope, "WHERE")).Message;
        Assert.Equal("Whole-row reference to 'dbo.Orders' in WHERE is forbidden because table 'dbo.Orders' contains masked columns.".Replace("'dbo.Orders' in", "'Orders' in"), Whole("dbo.Orders", null, Col("Orders")));
        Assert.Contains("Whole-row reference to 'dbo.Orders'", Whole("dbo.Orders", null, Col("dbo", "Orders").Name.Parts.Count == 2 ? Col("dbo.Orders") : Col("x")), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference to 'o'", Whole("dbo.Orders", "o", Col("o")), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference to 'O'", Whole("dbo.Orders", "o", Col("O")), StringComparison.Ordinal);
        AssertClean(Col("other"), v);
        var noMasks = Visitor((t, c) => false);
        AssertClean(Col("Orders"), noMasks);
        var off = Visitor((t, c) => false, b => { b.MaskedTables.Add("dbo.Orders"); b.WholeRow = false; });
        AssertClean(Col("Orders"), off);
        var columns = Visitor((t, c) => t == "dbo.Orders" && c == "ssn", b => b.Columns = t => new[] { "id", "ssn" });
        Assert.Contains("Whole-row reference", Assert.Throws<SecurityException>(() => Call<object?>(columns, "CheckNoMaskedColumnReferences", "dbo.Orders", null, Col("Orders"), "WHERE")).Message, StringComparison.Ordinal);
        var columnsClean = Visitor((t, c) => false, b => b.Columns = t => new[] { "id", "name" });
        AssertClean(Col("Orders"), columnsClean);
        var nullColumns = Visitor((t, c) => false, b => b.Columns = t => null);
        AssertClean(Col("Orders"), nullColumns);
    }

    [Fact]
    public void QualifiedWildcards_AreWholeRowReferences_OfTheTargetOrAnyMaskedTable()
    {
        var v = Visitor((t, c) => false, b => b.MaskedTables.Add("dbo.Orders"));
        string Wild(string? alias, string table, params string[] qualifier) =>
            Assert.Throws<SecurityException>(() => Call<object?>(v, "CheckNoMaskedColumnReferences", table, alias, new WildcardSelectItem(new SqlQualifiedName(qualifier)), "WHERE")).Message;
        Assert.Equal("Whole-row reference to 'Orders.*' in WHERE is forbidden because table 'dbo.Orders' contains masked columns.", Wild(null, "dbo.Orders", "Orders"));
        Assert.Equal("Whole-row reference to 'dbo.Orders.*' in WHERE is forbidden because table 'dbo.Orders' contains masked columns.", Wild(null, "dbo.Orders", "dbo", "Orders"));
        Assert.Contains("Whole-row reference to 'o.*'", Wild("o", "dbo.Orders", "o"), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference to 'dbo.o.*'", Wild("o", "dbo.Orders", "dbo", "o"), StringComparison.Ordinal);
        AssertClean(new WildcardSelectItem(null), v);
        AssertClean(new WildcardSelectItem(new SqlQualifiedName("other")), v);
        var other = Visitor((t, c) => false, b => b.MaskedTables.Add("t2"));
        Assert.Contains("Whole-row reference to 't2.*'", Assert.Throws<SecurityException>(() =>
            Call<object?>(other, "CheckNoMaskedColumnReferences", "dbo.Orders", null, new WildcardSelectItem(new SqlQualifiedName("t2")), "WHERE")).Message, StringComparison.Ordinal);
        // a wildcard of the target is only a violation when the target has masks
        var noMasks = Visitor((t, c) => false);
        AssertClean(new WildcardSelectItem(new SqlQualifiedName("Orders")), noMasks);
        AssertClean(new WildcardSelectItem(new SqlQualifiedName("o")), noMasks);
        var aliasOnlyMasked = Visitor((t, c) => false, b => b.MaskedTables.Add("dbo.Orders"));
        Assert.Throws<SecurityException>(() => Call<object?>(aliasOnlyMasked, "CheckNoMaskedColumnReferences", "dbo.Orders", "al", new WildcardSelectItem(new SqlQualifiedName("al")), "WHERE"));
        Assert.Throws<SecurityException>(() => Call<object?>(aliasOnlyMasked, "CheckNoMaskedColumnReferences", "dbo.Orders", "al", new WildcardSelectItem(new SqlQualifiedName("x", "al")), "WHERE"));
        Assert.Throws<SecurityException>(() => Call<object?>(aliasOnlyMasked, "CheckNoMaskedColumnReferences", "dbo.Orders", "", new WildcardSelectItem(new SqlQualifiedName("Orders")), "WHERE"));
    }

    [Fact]
    public void TheDmlAndPredicateEntryPoints_HonourTheirSwitches()
    {
        var s = Col("ssn");
        var on = Visitor();
        Assert.Throws<SecurityException>(() => Call<object?>(on, "EnsureNoMaskedColumnReferences", "dbo.Orders", null, s, "UPDATE SET"));
        Assert.Throws<SecurityException>(() => Call<object?>(on, "EnsureNoMaskedColumnReferencesInPredicate", "dbo.Orders", null, s, "WHERE"));
        var off = Visitor(null, b => { b.InDml = false; b.InPredicates = false; });
        Call<object?>(off, "EnsureNoMaskedColumnReferences", "dbo.Orders", null, s, "UPDATE SET");
        Call<object?>(off, "EnsureNoMaskedColumnReferencesInPredicate", "dbo.Orders", null, s, "WHERE");
        var dmlOnly = Visitor(null, b => b.InPredicates = false);
        Assert.Throws<SecurityException>(() => Call<object?>(dmlOnly, "EnsureNoMaskedColumnReferences", "dbo.Orders", null, s, "UPDATE SET"));
        Call<object?>(dmlOnly, "EnsureNoMaskedColumnReferencesInPredicate", "dbo.Orders", null, s, "WHERE");
        var predOnly = Visitor(null, b => b.InDml = false);
        Call<object?>(predOnly, "EnsureNoMaskedColumnReferences", "dbo.Orders", null, s, "UPDATE SET");
        Assert.Throws<SecurityException>(() => Call<object?>(predOnly, "EnsureNoMaskedColumnReferencesInPredicate", "dbo.Orders", null, s, "WHERE"));
    }

    [Fact]
    public void CollectTableSources_WalksJoinsSetOperationsAndLateralSources()
    {
        static NamedTableSource T(string n) => new NamedTableSource(new SqlQualifiedName("dbo", n), new SqlIdentifier("a" + n));
        var collect = typeof(AstSecurityVisitor).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "CollectTableSources").ToArray();
        List<(string, string?)> Run(object arg)
        {
            var list = new List<(string TableName, string? Alias)>();
            var m = collect.Single(c => c.GetParameters()[0].ParameterType.IsInstanceOfType(arg) || c.GetParameters()[0].ParameterType == (arg is TableSource || arg is null ? typeof(TableSource) : typeof(QueryBody)));
            m.Invoke(null, new[] { arg, list });
            return list.Select(x => (x.TableName, x.Alias)).ToList();
        }

        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA") }, Run(T("A")));
        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA"), ("dbo.B", "aB") },
            Run(new JoinedTableSource(T("A"), JoinType.Inner, T("B"), null)));
        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA"), ("dbo.B", "aB"), ("dbo.C", "aC") },
            Run(new JoinedTableSource(new JoinedTableSource(T("A"), JoinType.Inner, T("B"), null), JoinType.LeftOuter, T("C"), null)));
        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA") }, Run(new LateralTableSource(Select(Spec(from: T("A"))), new SqlIdentifier("l"))));
        Assert.Empty(Run(new SubqueryTableSource(Select(Spec(from: T("A"))), new SqlIdentifier("s"))));

        var setOp = new SetOperationQuery(Spec(from: T("A")), SetOperator.Union, false, Spec(from: T("B")));
        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA"), ("dbo.B", "aB") }, Run(setOp));
        Assert.Equal(new (string, string?)[] { ("dbo.X", null) }, Run(new TableQueryBody(new SqlQualifiedName("dbo", "X"))));
        Assert.Equal(new (string, string?)[] { ("dbo.A", "aA") }, Run(Spec(from: T("A"))));
        Assert.Empty(Run(Spec(from: null)));
        Assert.Empty(Run(new ValuesQueryBody(new[] { new RowValueExpression(new Expression[] { Int(1) }) })));

        var nulls = new List<(string, string?)>();
        collect.Single(m => m.GetParameters()[0].ParameterType == typeof(QueryBody)).Invoke(null, new object?[] { null, nulls });
        collect.Single(m => m.GetParameters()[0].ParameterType == typeof(TableSource)).Invoke(null, new object?[] { null, nulls });
        Assert.Empty(nulls);
    }

    [Fact]
    public void JoinConditions_OfAJoinTree_AreCheckedForEveryTable()
    {
        var s = Col("ssn");
        var a = new NamedTableSource(new SqlQualifiedName("dbo", "A"), null);
        var b = new NamedTableSource(new SqlQualifiedName("dbo", "B"), null);
        var tables = new List<(string TableName, string? Alias)> { ("dbo.A", null), ("dbo.B", null) };
        var v = Visitor();
        void Check(TableSource? from) => Call<object?>(v, "CheckJoinConditions", from, tables);
        Check(null);
        Check(a);
        Assert.Throws<SecurityException>(() => Check(new JoinedTableSource(a, JoinType.Inner, b, new OnJoinCondition(s))));
        Assert.Throws<SecurityException>(() => Check(new JoinedTableSource(a, JoinType.Inner, b, new UsingJoinCondition(new[] { new SqlIdentifier("ssn") }))));
        Assert.Throws<SecurityException>(() => Check(new JoinedTableSource(new JoinedTableSource(a, JoinType.Inner, b, new OnJoinCondition(s)), JoinType.Inner, b, null)));
        Assert.Throws<SecurityException>(() => Check(new JoinedTableSource(a, JoinType.Inner, new JoinedTableSource(a, JoinType.Inner, b, new OnJoinCondition(s)), null)));
        Check(new JoinedTableSource(a, JoinType.Inner, b, new OnJoinCondition(Col("id"))));
        Check(new JoinedTableSource(a, JoinType.Cross, b, null));
    }
    // ---- audit redaction and truncation ----

    [Fact]
    public void RedactSqlLiterals_ReplacesEveryLiteralKind_AndKeepsTheRest()
    {
        const string sql = "SELECT id /* c */ FROM t WHERE a = 'x' AND b > 42 AND c < 1.5 AND d = 1e3 AND e = X'ab' AND f = U&'d' AND g = $$z$$ -- end";
        string redacted = AstSecurityVisitor.RedactSqlLiterals(sql);
        Assert.Equal("SELECT id /* c */ FROM t WHERE a = @p_redacted AND b > @p_redacted AND c < @p_redacted AND d = @p_redacted AND e = @p_redacted AND f = @p_redacted AND g = @p_redacted -- end", redacted);
        Assert.Equal("SELECT a1, b2 FROM t3", AstSecurityVisitor.RedactSqlLiterals("SELECT a1, b2 FROM t3"));
        Assert.Equal("a = @p_redacted", AstSecurityVisitor.RedactSqlLiterals("a = 7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public void RedactSqlLiterals_EmptyInput_IsEmpty(string? sql) => Assert.Equal(string.Empty, AstSecurityVisitor.RedactSqlLiterals(sql));

    [Theory]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT a FROM t WHERE b = 'x' `")]
    [InlineData("SELECT # secret")]
    public void RedactSqlLiterals_UnlexableText_IsRedactedAsAWhole(string sql)
    {
        string redacted = AstSecurityVisitor.RedactSqlLiterals(sql);
        Assert.DoesNotContain("unterminated", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", redacted, StringComparison.Ordinal);
        Assert.Equal(AstSecurityVisitor.RedactSqlLiterals("SELECT 'unterminated"), redacted);
    }

    [Fact]
    public void TruncateForAudit_CutsOnlyWhatExceedsTheLimit()
    {
        Assert.Equal("abcde", AstSecurityVisitor.TruncateForAudit("abcde", 5));
        Assert.Equal("abcde", AstSecurityVisitor.TruncateForAudit("abcde", 6));
        Assert.Equal("abcd...[TRUNCATED]", AstSecurityVisitor.TruncateForAudit("abcde", 4));
        Assert.Equal("", AstSecurityVisitor.TruncateForAudit("", 0));
        Assert.Equal("...[TRUNCATED]", AstSecurityVisitor.TruncateForAudit("a", 0));
        Assert.Equal(new string('a', 4096), AstSecurityVisitor.TruncateForAudit(new string('a', 4096)));
        Assert.Equal(new string('a', 4096) + "...[TRUNCATED]", AstSecurityVisitor.TruncateForAudit(new string('a', 4097)));
    }
}
