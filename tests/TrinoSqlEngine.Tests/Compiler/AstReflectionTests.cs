using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for <see cref="AstReflection"/>: structural equality, the node walk, the child enumeration that the
/// capability validator uses, the referenced-column analysis of policy expressions, and the stable fingerprint.
/// </summary>
[Trait("Category", "SqlGate")]
public class AstReflectionTests
{
    /// <summary>A node whose property is a plain enumerable (not a list), which real nodes never use but the walkers must still enter.</summary>
    private sealed record EnumerableHolder(IEnumerable<Expression> Items) : Expression;

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p)).ToList()));

    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);

    private static IEnumerable<Expression> Lazy(params Expression[] items)
    {
        foreach (var item in items) yield return item;
    }

    // ---- StructurallyEqual ----

    [Fact]
    public void StructurallyEqual_SameInstanceAndEqualRecords_AreEqual()
    {
        var a = new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1));
        Assert.True(AstReflection.StructurallyEqual(a, a));
        Assert.True(AstReflection.StructurallyEqual(a, new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1))));
        Assert.True(AstReflection.StructurallyEqual(null, null));
    }

    [Fact]
    public void StructurallyEqual_NullOrDifferentTypes_AreNotEqual()
    {
        Assert.False(AstReflection.StructurallyEqual(Int(1), null));
        Assert.False(AstReflection.StructurallyEqual(null, Int(1)));
        Assert.False(AstReflection.StructurallyEqual(Int(1), Col("a")));
        Assert.False(AstReflection.StructurallyEqual(1, 1L));
    }

    [Fact]
    public void StructurallyEqual_DifferentValues_AreNotEqual()
    {
        Assert.False(AstReflection.StructurallyEqual(Int(1), Int(2)));
        Assert.False(AstReflection.StructurallyEqual(
            new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1)), new BinaryExpression(Col("a"), BinaryOperator.NotEqual, Int(1))));
        Assert.False(AstReflection.StructurallyEqual(Col("a"), Col("b")));
        Assert.True(AstReflection.StructurallyEqual(new LiteralExpression(1.5m, LiteralType.Decimal), new LiteralExpression(1.5m, LiteralType.Decimal)));
        Assert.False(AstReflection.StructurallyEqual(new LiteralExpression(1.5m, LiteralType.Decimal), new LiteralExpression(2.5m, LiteralType.Decimal)));
    }

    [Fact]
    public void StructurallyEqual_Lists_AreComparedInOrderAndByLength()
    {
        Expression[] ab = { Int(1), Int(2) };
        Assert.True(AstReflection.StructurallyEqual(ab, new Expression[] { Int(1), Int(2) }));
        Assert.False(AstReflection.StructurallyEqual(ab, new Expression[] { Int(2), Int(1) }));
        Assert.False(AstReflection.StructurallyEqual(ab, new Expression[] { Int(1) }));              // right is shorter
        Assert.False(AstReflection.StructurallyEqual(ab, new Expression[] { Int(1), Int(2), Int(3) })); // right is longer
        Assert.False(AstReflection.StructurallyEqual(new RowValueExpression(ab), new RowValueExpression(new Expression[] { Int(1) })));
    }

    [Fact]
    public void StructurallyEqual_ValueTypeNodes_AreComparedByTheirProperties()
    {
        Assert.True(AstReflection.StructurallyEqual(new SecurityPredicateId("dbo.t", 1), new SecurityPredicateId("dbo.t", 1)));
        Assert.False(AstReflection.StructurallyEqual(new SecurityPredicateId("dbo.t", 1), new SecurityPredicateId("dbo.t", 2)));
        Assert.False(AstReflection.StructurallyEqual(new SecurityPredicateId("dbo.t", 1), new SecurityPredicateId("dbo.u", 1)));
    }

    // ---- Walk ----

    private static List<object> Visited(object? root, Func<object, bool>? descend = null)
    {
        var seen = new List<object>();
        AstReflection.Walk(root, n =>
        {
            seen.Add(n);
            return descend?.Invoke(n) ?? true;
        });
        return seen;
    }

    [Fact]
    public void Walk_VisitsNestedNodesAndListElements_AndNothingForNullOrStrings()
    {
        Assert.Empty(Visited(null));
        Assert.Empty(Visited("text"));
        var tree = new BinaryExpression(Col("a"), BinaryOperator.Equal, new FunctionCallExpression(new SqlQualifiedName("f"), new Expression[] { Int(1), Int(2) }));

        var seen = Visited(tree);

        Assert.Equal(2, seen.OfType<LiteralExpression>().Count());
        Assert.Single(seen.OfType<ColumnReference>());
        Assert.Contains(tree, seen);
    }

    [Fact]
    public void Walk_EntersPlainEnumerables()
    {
        var seen = Visited(new EnumerableHolder(Lazy(Int(1), Int(2))));
        Assert.Equal(2, seen.OfType<LiteralExpression>().Count());
        Assert.Equal(2, Visited(Lazy(Int(1), Int(2))).Count);
    }

    [Fact]
    public void Walk_SkipsTheChildrenOfANodeTheVisitorRejects_AndForeignObjects()
    {
        var tree = new ParenthesizedExpression(new BinaryExpression(Int(1), BinaryOperator.Add, Int(2)));

        var seen = Visited(tree, n => n is not BinaryExpression);

        Assert.DoesNotContain(seen, n => n is LiteralExpression);
        Assert.Empty(Visited(new object()));
        Assert.Empty(Visited(new List<object> { new object() }));
    }

    [Fact]
    public void Walk_VisitsValueTypeNodesOfTheEngine()
    {
        var predicate = new SecurityPredicateExpression(Int(1), new SecurityPredicateId("dbo.t", 0), SecurityScope.Root);
        Assert.Contains(Visited(predicate), n => n is SecurityPredicateId);
    }

    [Fact]
    public void Collect_ReturnsAllNodesOfTheType()
    {
        var tree = new BinaryExpression(Col("a"), BinaryOperator.Equal, new BinaryExpression(Col("b"), BinaryOperator.Add, Int(1)));
        Assert.Equal(new[] { "a", "b" }, AstReflection.Collect<ColumnReference>(tree).Select(c => c.Name.SimpleName));
    }

    // ---- Children ----

    private static List<object> ChildrenOf(object node)
    {
        var seen = new List<object>();
        AstReflection.Children(node, seen, static (child, list) => list.Add(child));
        return seen;
    }

    [Fact]
    public void Children_OfANode_AreItsNodeAndListProperties_WithoutScalarProperties()
    {
        var node = new InListExpression(Col("a"), new Expression[] { Int(1) }, IsNotIn: true);

        var children = ChildrenOf(node);

        Assert.Equal(2, children.Count);
        Assert.IsType<ColumnReference>(children[0]);
        Assert.IsAssignableFrom<IReadOnlyList<Expression>>(children[1]);
    }

    [Fact]
    public void Children_OfAList_AreItsNonNullElements()
    {
        var list = new List<Expression?> { Int(1), null, Int(2) };
        Assert.Equal(2, ChildrenOf(list).Count);
    }

    [Fact]
    public void Children_OfAPlainEnumerable_AreItsNonNullElements()
    {
        Assert.Equal(2, ChildrenOf(Lazy(Int(1), Int(2))).Count);
    }

    [Fact]
    public void Children_OfAForeignObjectOrAString_AreEmpty()
    {
        Assert.Empty(ChildrenOf(new object()));
        Assert.Empty(ChildrenOf("text"));
    }

    [Fact]
    public void Children_OfANode_SkipNullProperties()
    {
        var node = new LikeExpression(Col("a"), Col("b"));   // Escape is null
        Assert.Equal(2, ChildrenOf(node).Count);
    }

    // ---- ReferencedTargetColumns ----

    [Fact]
    public void ReferencedTargetColumns_OutsideASubquery_AreAllColumnNamesLowerCased()
    {
        var expression = new BinaryExpression(Col("Region"), BinaryOperator.Equal, Col("o", "Status"));
        Assert.Equal(new[] { "region", "status" }, AstReflection.ReferencedTargetColumns(expression).OrderBy(x => x));
    }

    [Fact]
    public void ReferencedTargetColumns_InsideASubquery_AreOnlyThoseQualifiedWithTheTargetAlias()
    {
        var inner = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(Col("Hidden"), null) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new BinaryExpression(
                    new BinaryExpression(Col(RowFilterAliases.Target, "OrderId"), BinaryOperator.Equal, Col("other", "Id")),
                    BinaryOperator.And,
                    new BinaryExpression(Col("c", "d", "Deep"), BinaryOperator.Equal, Col(RowFilterAliases.Target.ToUpperInvariant(), "Upper"))),
                null, null), null, null);
        var expression = new BinaryExpression(Col("Outer"), BinaryOperator.And, new ExistsExpression(inner));

        var columns = AstReflection.ReferencedTargetColumns(expression).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "orderid", "outer", "upper" }, columns);
    }

    [Fact]
    public void ReferencedTargetColumns_AreFoundInListsAndPlainEnumerables()
    {
        var inList = new InListExpression(Col("A"), new Expression[] { Col("B"), Col("C") }, false);
        Assert.Equal(new[] { "a", "b", "c" }, AstReflection.ReferencedTargetColumns(inList).OrderBy(x => x));
        Assert.Equal(new[] { "x", "y" }, AstReflection.ReferencedTargetColumns(new EnumerableHolder(Lazy(Col("X"), Col("Y")))).OrderBy(x => x));
    }

    // ---- Fingerprint ----

    [Fact]
    public void Fingerprint_IsStable_AndDependsOnShapeTypeAndValues()
    {
        var a = new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1));
        var same = new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1));

        Assert.Equal(AstReflection.Fingerprint(a), AstReflection.Fingerprint(same));
        Assert.Equal(64, AstReflection.Fingerprint(a).Length);
        Assert.NotEqual(AstReflection.Fingerprint(a), AstReflection.Fingerprint(new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(2))));
        Assert.NotEqual(AstReflection.Fingerprint(a), AstReflection.Fingerprint(new BinaryExpression(Col("b"), BinaryOperator.Equal, Int(1))));
        Assert.NotEqual(AstReflection.Fingerprint(a), AstReflection.Fingerprint(new BinaryExpression(Col("a"), BinaryOperator.NotEqual, Int(1))));
        Assert.NotEqual(AstReflection.Fingerprint(new RowValueExpression(new Expression[] { Int(1), Int(2) })), AstReflection.Fingerprint(new RowValueExpression(new Expression[] { Int(12) })));
        Assert.Equal(AstFingerprint.Compute(a), AstReflection.Fingerprint(a));
    }

    [Fact]
    public void FingerprintMemoized_ReturnsTheFingerprintOfTheInstance()
    {
        var a = new BinaryExpression(Col("a"), BinaryOperator.Equal, Int(1));
        Assert.Equal(AstReflection.Fingerprint(a), AstReflection.FingerprintMemoized(a));
        Assert.Equal(AstReflection.FingerprintMemoized(a), AstReflection.FingerprintMemoized(a));
    }
}
