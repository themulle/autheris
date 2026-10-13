using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for <see cref="LiteralColumnTypeAnnotator"/> (CR-ADG-09): a literal compared with a catalog column carries the
/// column type, and only when the column is unambiguous; anything else keeps the value type.
/// </summary>
[Trait("Category", "SqlGate")]
public class LiteralColumnTypeAnnotatorTests
{
    private static SqlIdentifier Id(string name) => new(name);

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(Id).ToList()));

    private static LiteralExpression Lit(string v = "x") => new(v, LiteralType.String);

    private static NamedTableSource Table(string name, string? alias = null) => new(new SqlQualifiedName("dbo", name), alias is null ? null : Id(alias));

    private static JoinedTableSource Join(TableSource l, TableSource r) => new(l, JoinType.Cross, r, null);

    private static SelectStatement Select(TableSource from, Expression where) =>
        new(null, new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, from, where, null, null), null, null);

    private static Expression WhereOf(SqlNode statement) => ((QuerySpecification)((SelectStatement)statement).Body).Where!;

    private static Expression Annotated(TableSource from, Expression where) =>
        WhereOf(new LiteralColumnTypeAnnotator(Catalog()).Visit(Select(from, where)));

    private static LiteralExpression RightLiteral(Expression e) => (LiteralExpression)((BinaryExpression)e).Right;

    private static void AssertKept(LiteralExpression literal)
    {
        Assert.Null(literal.ColumnType);
        Assert.False(literal.ColumnTypeUnknown);
    }

    [Fact]
    public void UnqualifiedColumn_OfASingleCatalogTable_AnnotatesTheLiteral_OnEitherSide()
    {
        var right = RightLiteral(Annotated(Table("Orders"), new BinaryExpression(Col("Region"), BinaryOperator.Equal, Lit())));
        Assert.Equal("nvarchar(20)", right.ColumnType);

        var left = (LiteralExpression)((BinaryExpression)Annotated(Table("Orders"), new BinaryExpression(Lit(), BinaryOperator.Equal, Col("Region")))).Left;
        Assert.Equal("nvarchar(20)", left.ColumnType);
    }

    [Fact]
    public void OnlyTheLiteralSide_IsRewritten_TheColumnSideIsKept()
    {
        var where = (BinaryExpression)Annotated(Table("Orders"), new BinaryExpression(Col("Region"), BinaryOperator.Equal, Lit()));
        Assert.IsType<ColumnReference>(where.Left);
    }

    [Fact]
    public void QualifiedColumn_WithAUniqueAlias_AnnotatesTheLiteral()
    {
        var join = Join(Table("Orders", "o"), Table("Entitlements", "e"));
        Assert.Equal("nvarchar(20)", RightLiteral(Annotated(join, new BinaryExpression(Col("o", "Region"), BinaryOperator.Equal, Lit()))).ColumnType);
        Assert.Equal("int", RightLiteral(Annotated(join, new BinaryExpression(Col("e", "OrderId"), BinaryOperator.Equal, Lit("1")))).ColumnType);
    }

    [Fact]
    public void QualifiedColumn_WithADuplicateAliasOrAnUnknownAlias_KeepsTheValueType()
    {
        var duplicate = Join(Table("Orders", "a"), Table("Entitlements", "a"));
        AssertKept(RightLiteral(Annotated(duplicate, new BinaryExpression(Col("a", "Id"), BinaryOperator.Equal, Lit("1")))));
        AssertKept(RightLiteral(Annotated(Table("Orders", "o"), new BinaryExpression(Col("z", "Region"), BinaryOperator.Equal, Lit()))));
    }

    [Fact]
    public void QualifiedColumn_NotInTheCatalogEntry_KeepsTheValueType()
    {
        AssertKept(RightLiteral(Annotated(Table("Orders", "o"), new BinaryExpression(Col("o", "NoSuchColumn"), BinaryOperator.Equal, Lit()))));
    }

    [Fact]
    public void UnqualifiedColumn_OnlyTheTablesThatHaveItCount()
    {
        // Region exists only in Orders: the join with Entitlements does not make it ambiguous
        var join = Join(Table("Orders"), Table("Entitlements"));
        Assert.Equal("nvarchar(20)", RightLiteral(Annotated(join, new BinaryExpression(Col("Region"), BinaryOperator.Equal, Lit()))).ColumnType);
    }

    [Fact]
    public void UnqualifiedColumn_OfTwoTables_IsAmbiguous_AndKeepsTheValueType()
    {
        var join = Join(Table("Orders"), Table("Entitlements"));
        AssertKept(RightLiteral(Annotated(join, new BinaryExpression(Col("Id"), BinaryOperator.Equal, Lit("1")))));
    }

    [Fact]
    public void UnqualifiedColumn_NextToAnOpaqueSource_KeepsTheValueType()
    {
        var derivedBody = Select(Table("Lookup"), new BinaryExpression(Int(1), BinaryOperator.Equal, Int(1)));
        TableSource[] opaque =
        {
            new SubqueryTableSource(derivedBody, Id("d")),
            new LateralTableSource(derivedBody, Id("l"))
        };

        foreach (var source in opaque)
        {
            var join = Join(Table("Orders"), source);
            AssertKept(RightLiteral(Annotated(join, new BinaryExpression(Col("Region"), BinaryOperator.Equal, Lit()))));
        }
    }

    private static LiteralExpression Int(long v) => new(v, LiteralType.Integer);

    [Fact]
    public void IsDistinctFrom_AnnotatesTheLiteralOnEitherSide_AndKeepsAnUnchangedNodeAsIs()
    {
        var right = (IsDistinctFromExpression)Annotated(Table("Orders"), new IsDistinctFromExpression(Col("Region"), Lit(), false));
        Assert.Equal("nvarchar(20)", ((LiteralExpression)right.Right).ColumnType);

        var left = (IsDistinctFromExpression)Annotated(Table("Orders"), new IsDistinctFromExpression(Lit(), Col("Region"), false));
        Assert.Equal("nvarchar(20)", ((LiteralExpression)left.Left).ColumnType);

        var untouched = new IsDistinctFromExpression(Col("Region"), Col("Status"), false);
        Assert.Same(untouched, new LiteralColumnTypeAnnotator(Catalog()).VisitIsDistinctFromExpression(untouched));
    }

    [Fact]
    public void Binary_WithoutAnyAnnotation_IsReturnedAsTheSameInstance()
    {
        var untouched = new BinaryExpression(Col("Region"), BinaryOperator.Equal, Col("Status"));
        Assert.Same(untouched, new LiteralColumnTypeAnnotator(Catalog()).VisitBinaryExpression(untouched));
    }

    [Fact]
    public void Between_AnnotatesEachBoundIndependently_AndKeepsAnUnchangedNodeAsIs()
    {
        var onlyLower = (BetweenExpression)Annotated(Table("Orders"), new BetweenExpression(Col("Region"), Lit("a"), Col("Status"), false));
        Assert.Equal("nvarchar(20)", ((LiteralExpression)onlyLower.Lower).ColumnType);
        Assert.IsType<ColumnReference>(onlyLower.Upper);

        var onlyUpper = (BetweenExpression)Annotated(Table("Orders"), new BetweenExpression(Col("Region"), Col("Status"), Lit("z"), false));
        Assert.IsType<ColumnReference>(onlyUpper.Lower);
        Assert.Equal("nvarchar(20)", ((LiteralExpression)onlyUpper.Upper).ColumnType);

        var untouched = new BetweenExpression(Col("Region"), Col("Status"), Col("Email"), false);
        Assert.Same(untouched, new LiteralColumnTypeAnnotator(Catalog()).VisitBetweenExpression(untouched));
    }

    [Fact]
    public void ADeleteOrUpdateTarget_IsAScopeForItsOwnColumns()
    {
        var where = new BinaryExpression(Col("Region"), BinaryOperator.Equal, Lit());
        var visited = (DeleteStatement)new LiteralColumnTypeAnnotator(Catalog()).Visit(new DeleteStatement(Table("Orders"), where));
        Assert.Equal("nvarchar(20)", RightLiteral(visited.Where!).ColumnType);

        var update = (UpdateStatement)new LiteralColumnTypeAnnotator(Catalog()).Visit(
            new UpdateStatement(Table("Orders"), new[] { new UpdateAssignment(Id("Status"), Lit("a")) }, where));
        Assert.Equal("nvarchar(20)", RightLiteral(update.Where!).ColumnType);
    }
}
