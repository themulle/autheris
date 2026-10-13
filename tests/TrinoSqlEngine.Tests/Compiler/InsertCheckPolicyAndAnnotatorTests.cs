using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for <see cref="InsertCheckPolicy"/> (CR-ADG-42) and <see cref="PolicyColumnTypeAnnotator"/> (CR-ADG-09):
/// the byte-exact rewrite of string equality/IN, the verifier that never trusts the rewrite, and the column type annotation.
/// </summary>
[Trait("Category", "SqlGate")]
public class InsertCheckPolicyAndAnnotatorTests
{
    private const string Rejected = "An INSERT check on a string column supports only equality and IN with a bound value.";
    private static readonly SqlIdentifier Alias = new("ins", true);
    private static readonly DialectCapabilities Caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);

    private static bool IsText(string column) => column is "name" or "name2";

    private static ColumnReference Col(string table, string column) => new(new SqlQualifiedName(new[] { new SqlIdentifier(table, true), new SqlIdentifier(column) }));

    private static Expression Param(string name = "p") => new PolicyParameterExpression(name, SqlParameterType.String);

    private static Expression Eq(Expression l, Expression r) => new BinaryExpression(l, BinaryOperator.Equal, r);

    private static Expression Int(int v) => new LiteralExpression(v, LiteralType.Integer);

    private static Expression Rewrite(Expression e) => InsertCheckPolicy.Rewrite(e, Alias, IsText, Caps);

    private static void Validate(Expression e) => InsertCheckPolicy.Validate(e, Alias, IsText, Caps);

    private static void AssertRejected(Action action) =>
        Assert.Equal(Rejected, Assert.Throws<InsertCheckPolicy.UnsupportedStringPredicateException>(action).Message);

    // ---- Rewrite ----

    [Fact]
    public void Rewrite_StringEquality_BecomesTheByteExactComparison_InParentheses()
    {
        var result = Rewrite(Eq(Col("ins", "name"), Param()));

        var paren = Assert.IsType<ParenthesizedExpression>(result);
        Assert.True(AstReflection.StructurallyEqual(paren.Expression, TenantPredicateFactory.ExactEquals(Caps, Col("ins", "name"), Param())));
    }

    [Fact]
    public void Rewrite_ValueOnTheLeft_IsAlsoByteExact()
    {
        var result = Rewrite(Eq(Param(), Col("ins", "name")));

        var paren = Assert.IsType<ParenthesizedExpression>(result);
        Assert.True(AstReflection.StructurallyEqual(paren.Expression, TenantPredicateFactory.ExactEquals(Caps, Col("ins", "name"), Param())));
    }

    [Fact]
    public void Rewrite_InList_BecomesAnOrChainOfByteExactEqualities_InTheItemOrder()
    {
        var list = new InListExpression(Col("ins", "name"), new[] { Param("a"), Param("b"), Param("c") }, false);

        var result = Assert.IsType<ParenthesizedExpression>(Rewrite(list));

        Expression Item(string n) => new ParenthesizedExpression(TenantPredicateFactory.ExactEquals(Caps, Col("ins", "name"), Param(n)));
        Expression expected = new BinaryExpression(new BinaryExpression(Item("a"), BinaryOperator.Or, Item("b")), BinaryOperator.Or, Item("c"));
        Assert.True(AstReflection.StructurallyEqual(result.Expression, expected));
    }

    [Fact]
    public void Rewrite_InListWithOneItem_IsOneByteExactEquality()
    {
        var list = new InListExpression(Col("ins", "name"), new[] { Param("a") }, false);

        var result = Assert.IsType<ParenthesizedExpression>(Rewrite(list));

        var item = Assert.IsType<ParenthesizedExpression>(result.Expression);
        Assert.True(AstReflection.StructurallyEqual(item.Expression, TenantPredicateFactory.ExactEquals(Caps, Col("ins", "name"), Param("a"))));
    }

    [Fact]
    public void Rewrite_PolicyWithoutStringColumns_IsReturnedUnchanged_SameInstance()
    {
        var and = new BinaryExpression(Eq(Col("ins", "amount"), Int(1)), BinaryOperator.And, new ParenthesizedExpression(Eq(Col("ins", "qty"), Int(2))));
        Assert.Same(and, Rewrite(and));

        var not = new UnaryExpression(UnaryOperator.Not, Eq(Col("ins", "amount"), Int(1)));
        Assert.Same(not, Rewrite(not));

        var paren = new ParenthesizedExpression(Eq(Col("ins", "amount"), Int(1)));
        Assert.Same(paren, Rewrite(paren));
    }

    [Fact]
    public void Rewrite_RebuildsOnlyTheChangedBranches()
    {
        var plain = Eq(Col("ins", "amount"), Int(1));
        var and = new BinaryExpression(plain, BinaryOperator.And, Eq(Col("ins", "name"), Param()));

        var result = Assert.IsType<BinaryExpression>(Rewrite(and));

        Assert.Same(plain, result.Left);
        Assert.IsType<ParenthesizedExpression>(result.Right);
    }

    [Fact]
    public void Rewrite_NegatedNonStringPredicate_IsKept_ButANegatedStringPredicateIsRejected()
    {
        var not = new UnaryExpression(UnaryOperator.Not, Eq(Col("ins", "name"), Param()));
        AssertRejected(() => Rewrite(not));
        AssertRejected(() => Rewrite(new UnaryExpression(UnaryOperator.Not, new ParenthesizedExpression(new InListExpression(Col("ins", "name"), new[] { Param() }, false)))));
    }

    [Fact]
    public void Rewrite_NullTestOfAColumn_IsKept()
    {
        var test = new UnaryExpression(UnaryOperator.IsNull, Col("ins", "name"));
        Assert.Same(test, Rewrite(test));
    }

    [Theory]
    [InlineData(BinaryOperator.NotEqual)]
    [InlineData(BinaryOperator.LessThan)]
    [InlineData(BinaryOperator.GreaterThanOrEqual)]
    public void Rewrite_NonEqualityComparisonOfAStringColumn_IsRejected(BinaryOperator op)
    {
        AssertRejected(() => Rewrite(new BinaryExpression(Col("ins", "name"), op, Param())));
    }

    [Fact]
    public void Rewrite_ComparisonOfTwoStringColumns_OrOfAStringColumnWithANonValue_IsRejected()
    {
        AssertRejected(() => Rewrite(Eq(Col("ins", "name"), Col("ins", "name2"))));
        AssertRejected(() => Rewrite(Eq(Col("ins", "name"), new FunctionCallExpression(new SqlQualifiedName("lower"), new Expression[] { Param() }))));
        AssertRejected(() => Rewrite(Eq(Col("ins", "name"), Col("ins", "amount"))));
    }

    [Fact]
    public void Rewrite_FunctionOverAStringColumn_IsRejected()
    {
        AssertRejected(() => Rewrite(new FunctionCallExpression(new SqlQualifiedName("lower"), new Expression[] { Col("ins", "name") })));
    }

    [Fact]
    public void Rewrite_InListThatIsNotAPlainByteExactCandidate_IsRejected()
    {
        AssertRejected(() => Rewrite(new InListExpression(Col("ins", "name"), new[] { Param() }, true)));                          // NOT IN
        AssertRejected(() => Rewrite(new InListExpression(Col("ins", "name"), Array.Empty<Expression>(), false)));                 // empty
        AssertRejected(() => Rewrite(new InListExpression(Col("ins", "name"), new[] { Col("ins", "name2") }, false)));             // not a bound value
        AssertRejected(() => Rewrite(new InListExpression(
            new FunctionCallExpression(new SqlQualifiedName("lower"), new Expression[] { Param() }), new[] { Col("ins", "name") }, false)));   // operand is not the string column
    }

    [Fact]
    public void Rewrite_NestedSelectOfAnotherTable_IsNotAReadOfTheInsertedValues()
    {
        // an EXISTS over a derived select does not touch the inserted values even though it names a string-looking column of another table
        var inner = new SelectStatement(null,
            new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
                new NamedTableSource(new SqlQualifiedName("dbo", "t"), null), Eq(Col("ins", "name"), Param()), null, null), null, null);
        var exists = new ExistsExpression(inner);

        Assert.Same(exists, Rewrite(exists));
    }

    // ---- Validate (the verifier never trusts the rewrite) ----

    [Fact]
    public void Validate_AcceptsWhatRewriteProduces()
    {
        var rewritten = Rewrite(new BinaryExpression(
            Eq(Col("ins", "name"), Param()), BinaryOperator.And, new InListExpression(Col("ins", "name2"), new[] { Param("a"), Param("b") }, false)));

        Validate(rewritten);
    }

    [Fact]
    public void Validate_RejectsABareStringEquality()
    {
        AssertRejected(() => Validate(Eq(Col("ins", "name"), Param())));
    }

    [Fact]
    public void Validate_RejectsAnInList_BecauseOnlyTheRewrittenFormIsAccepted()
    {
        AssertRejected(() => Validate(new InListExpression(Col("ins", "name"), new[] { Param() }, false)));
    }

    [Fact]
    public void Validate_RejectsABareEqualityHiddenBehindAnAnd_WhoseLeftLeafIsNotAStringEquality()
    {
        // leftmost leaf compares two non-string operands (left == right == false): the chain is not a byte-exact equality
        var chain = new BinaryExpression(Eq(Col("ins", "amount"), Int(5)), BinaryOperator.And, Eq(Col("ins", "name"), Param()));
        AssertRejected(() => Validate(chain));
    }

    [Fact]
    public void Validate_RejectsAChainWhoseLeftLeafComparesTwoStringColumns()
    {
        var chain = new BinaryExpression(Eq(Col("ins", "name"), Col("ins", "name2")), BinaryOperator.And, Eq(Col("ins", "name"), Param()));
        AssertRejected(() => Validate(chain));
    }

    [Fact]
    public void Validate_RejectsAChainWhoseLeftLeafComparesAStringColumnWithANonValue()
    {
        var chain = new BinaryExpression(
            Eq(Col("ins", "name"), new FunctionCallExpression(new SqlQualifiedName("lower"), new Expression[] { Param() })),
            BinaryOperator.And, Eq(Col("ins", "name"), Param()));
        AssertRejected(() => Validate(chain));
    }

    [Fact]
    public void Validate_RejectsANegatedByteExactEquality()
    {
        var exact = new ParenthesizedExpression(TenantPredicateFactory.ExactEquals(Caps, Col("ins", "name"), Param()));
        Validate(exact);
        AssertRejected(() => Validate(new UnaryExpression(UnaryOperator.Not, exact)));
    }

    // ---- PolicyColumnTypeAnnotator ----

    private static readonly TableCatalogEntry Entry = new(
        new TableIdentity("dbo", "Orders"),
        ImmutableArray.Create(new CatalogColumn("Region", "nvarchar(20)"), new CatalogColumn("Id", "int")),
        null, 1);

    private static PolicyParameterExpression P(string name, string? columnType = null) => new(name, SqlParameterType.String, ColumnType: columnType);

    private static Expression Annotate(Expression e) => (Expression)new PolicyColumnTypeAnnotator(Entry).Visit(e);

    private static ColumnReference Own(string column) => new(new SqlQualifiedName(new[] { new SqlIdentifier(column) }));

    [Fact]
    public void Annotator_ComparisonOfATargetColumn_RecordsTheCatalogTypeOnThePolicyParameter()
    {
        var result = Assert.IsType<BinaryExpression>(Annotate(Eq(Own("REGION"), P("p"))));
        Assert.Equal("nvarchar(20)", Assert.IsType<PolicyParameterExpression>(result.Right).ColumnType);

        var reversed = Assert.IsType<BinaryExpression>(Annotate(Eq(P("p"), Own("Region"))));
        Assert.Equal("nvarchar(20)", Assert.IsType<PolicyParameterExpression>(reversed.Left).ColumnType);
    }

    [Fact]
    public void Annotator_TargetAliasQualifiedColumn_IsATargetColumn_ButAnotherQualifierIsNot()
    {
        var target = new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier(RowFilterAliases.Target), new SqlIdentifier("Region") }));
        var other = new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("other"), new SqlIdentifier("Region") }));

        Assert.Equal("nvarchar(20)", ((PolicyParameterExpression)((BinaryExpression)Annotate(Eq(target, P("p")))).Right).ColumnType);

        var untouched = Eq(other, P("p"));
        Assert.Same(untouched, Annotate(untouched));
    }

    [Fact]
    public void Annotator_AlreadyTypedParameter_OrUnknownColumn_IsLeftAlone_SameInstance()
    {
        var typed = Eq(Own("Region"), P("p", "varchar(5)"));
        Assert.Same(typed, Annotate(typed));

        var unknown = Eq(Own("Nope"), P("p"));
        Assert.Same(unknown, Annotate(unknown));
    }

    [Fact]
    public void Annotator_InList_AnnotatesEveryParameterItem_WhateverTheItemCount()
    {
        var two = Assert.IsType<InListExpression>(Annotate(new InListExpression(Own("Region"), new Expression[] { P("a"), P("b") }, false)));
        Assert.All(two.Items, i => Assert.Equal("nvarchar(20)", Assert.IsType<PolicyParameterExpression>(i).ColumnType));

        var mixed = Assert.IsType<InListExpression>(Annotate(new InListExpression(Own("Region"), new Expression[] { P("a", "varchar(5)"), P("b") }, false)));
        Assert.Equal("varchar(5)", ((PolicyParameterExpression)mixed.Items[0]).ColumnType);
        Assert.Equal("nvarchar(20)", ((PolicyParameterExpression)mixed.Items[1]).ColumnType);
    }

    [Fact]
    public void Annotator_InListThatNeedsNoChange_IsReturnedAsTheSameInstance()
    {
        var allTyped = new InListExpression(Own("Region"), new Expression[] { P("a", "varchar(5)"), Int(1) }, false);
        Assert.Same(allTyped, Annotate(allTyped));

        var unknownOperand = new InListExpression(Own("Nope"), new Expression[] { P("a") }, false);
        Assert.Same(unknownOperand, Annotate(unknownOperand));
    }

    [Fact]
    public void Annotator_InListOfAnUnknownOperand_StillAnnotatesNestedComparisons()
    {
        // the base visitor handles an operand without a type: a comparison nested in an item is still annotated
        var nested = Eq(Own("Region"), P("n"));
        var list = new InListExpression(Own("Nope"), new Expression[] { new ParenthesizedExpression(nested) }, false);

        var result = Assert.IsType<InListExpression>(Annotate(list));

        var inner = Assert.IsType<BinaryExpression>(Assert.IsType<ParenthesizedExpression>(result.Items[0]).Expression);
        Assert.Equal("nvarchar(20)", Assert.IsType<PolicyParameterExpression>(inner.Right).ColumnType);
    }
}
