using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation gate (M-12) tests for <see cref="DmlMaskedReadGuard"/>, <see cref="MergeAliasGuard"/>, <see cref="InsertCheckOption"/> and
/// <see cref="SqliteTypeAffinity"/>: the typed violation texts, the first violation wins, injected predicates and masks are not reads.
/// </summary>
[Trait("Category", "SqlGate")]
public class DmlGuardHelperTests
{
    private const string MaskedRead = "A masked column of the DML target is read by the statement.";
    private const string WholeRow = "A whole-row reference to a DML target with masked columns is not permitted.";

    private static readonly HashSet<string> Masked = new(StringComparer.Ordinal) { "Email" };
    private static readonly string[] Targets = { "t", "Orders" };

    private static ColumnReference Col(params string[] parts) => new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p)).ToList()));

    private static Expression Eq(Expression l, Expression r) => new BinaryExpression(l, BinaryOperator.Equal, r);

    private static Expression Int(int v) => new LiteralExpression(v, LiteralType.Integer);

    // ---- DmlMaskedReadGuard ----

    [Fact]
    public void NoMaskedColumns_NeverReportsAViolation()
    {
        Assert.Null(DmlMaskedReadGuard.FindViolation(new HashSet<string>(), Targets, Eq(Col("t", "Email"), Int(1))));
    }

    [Fact]
    public void CleanParts_ReportNoViolation()
    {
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, Eq(Col("t", "Id"), Int(1)), null));
    }

    [Fact]
    public void MaskedColumnOfTheTarget_IsReported_WithATypedValueFreeText()
    {
        Assert.Equal(MaskedRead, DmlMaskedReadGuard.FindViolation(Masked, Targets, Eq(Col("t", "Email"), Int(1))));
        Assert.Equal(MaskedRead, DmlMaskedReadGuard.FindViolation(Masked, Targets, Eq(Col("Email"), Int(1))));
    }

    [Fact]
    public void MaskedColumnNameOfASourceAlias_IsNotATargetRead()
    {
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, Eq(Col("src", "Email"), Int(1))));
    }

    [Fact]
    public void WholeRowReferenceOfTheTarget_IsReported()
    {
        Assert.Equal(WholeRow, DmlMaskedReadGuard.FindViolation(Masked, Targets, Eq(Col("ORDERS"), Int(1))));
    }

    [Fact]
    public void QualifiedWildcardOfTheTarget_IsReported()
    {
        Assert.Equal(WholeRow, DmlMaskedReadGuard.FindViolation(Masked, Targets, new WildcardSelectItem(new SqlQualifiedName("T"))));
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, new WildcardSelectItem(new SqlQualifiedName("src"))));
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, new WildcardSelectItem(null)));
    }

    [Fact]
    public void UsingJoinOnAMaskedColumn_IsReported_EvenWhenOtherColumnsAreListed()
    {
        var onlyOneMasked = new UsingJoinCondition(new[] { new SqlIdentifier("Id"), new SqlIdentifier("Email") });
        Assert.Equal(MaskedRead, DmlMaskedReadGuard.FindViolation(Masked, Targets, onlyOneMasked));
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, new UsingJoinCondition(new[] { new SqlIdentifier("Id") })));
    }

    [Fact]
    public void TheFirstViolationWins()
    {
        var first = Eq(Col("t", "Email"), Int(1));
        var second = new WildcardSelectItem(new SqlQualifiedName("t"));
        Assert.Equal(MaskedRead, DmlMaskedReadGuard.FindViolation(Masked, Targets, new object[] { first, second }));
        Assert.Equal(MaskedRead, DmlMaskedReadGuard.FindViolation(Masked, Targets, new object[] { new BinaryExpression(first, BinaryOperator.And, Eq(Col("t"), Int(1))) }));
        Assert.Equal(WholeRow, DmlMaskedReadGuard.FindViolation(Masked, Targets, new BinaryExpression(Eq(Col("t"), Int(1)), BinaryOperator.And, first)));
    }

    [Fact]
    public void InjectedSecurityPredicate_IsNotAUserRead()
    {
        var injected = new SecurityPredicateExpression(Eq(Col("t", "Email"), Int(1)), new SecurityPredicateId("dbo.Orders", 0), SecurityScope.DmlTarget);
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, injected));
    }

    [Fact]
    public void InjectedMask_IsNotAUserRead()
    {
        var mask = new MaskExpression(MaskKind.Nullify, (ColumnReference)Col("t", "Email"), new MaskArguments());
        Assert.Null(DmlMaskedReadGuard.FindViolation(Masked, Targets, mask));
    }

    // ---- MergeAliasGuard ----

    private static NamedTableSource Named(string name, string? alias = null) =>
        new(new SqlQualifiedName("dbo", name), alias is null ? null : new SqlIdentifier(alias));

    private static SelectStatement AnySelect() => new(null,
        new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) }, Named("x"), null, null, null), null, null);

    [Fact]
    public void SourceQualifiers_AreAliasOrTableName_OfEveryLeaf()
    {
        var source = new JoinedTableSource(
            new JoinedTableSource(Named("A"), JoinType.Inner, Named("B", "bb"), null),
            JoinType.Inner,
            new JoinedTableSource(new SubqueryTableSource(AnySelect(), new SqlIdentifier("sq")), JoinType.Cross, new LateralTableSource(AnySelect(), new SqlIdentifier("lat")), null),
            null);

        var qualifiers = new List<string>();
        MergeAliasGuard.CollectSourceQualifiers(source, qualifiers);

        Assert.Equal(new[] { "A", "bb", "sq", "lat" }, qualifiers);
    }

    [Theory]
    [InlineData("lat", false)]
    [InlineData("LAT", false)]
    [InlineData("sq", false)]
    [InlineData("A", false)]
    [InlineData("other", true)]
    public void IsDisjoint_DetectsACollisionWithAnyLeafOfTheSource(string targetName, bool disjoint)
    {
        var source = new JoinedTableSource(Named("A"), JoinType.Inner,
            new JoinedTableSource(new SubqueryTableSource(AnySelect(), new SqlIdentifier("sq")), JoinType.Cross, new LateralTableSource(AnySelect(), new SqlIdentifier("lat")), null), null);

        Assert.Equal(disjoint, MergeAliasGuard.IsDisjoint(source, new[] { targetName }));
    }

    // ---- InsertCheckOption ----

    private static SecurityPredicateExpression Check(SecurityScope scope = SecurityScope.InsertCheck) =>
        new(Eq(Col("x"), Int(1)), new SecurityPredicateId("dbo.Orders", 0), scope);

    private static QuerySpecification Row() => new(false, new SelectItem[] { new ColumnSelectItem(Int(1), null) }, null, null, null, null);

    private static InsertStatement Insert(Expression? where, QueryBody body, SqlIdentifier? alias = null) => new(
        Named("Orders"), null,
        new QuerySpecification(false, new SelectItem[] { new WildcardSelectItem(null) },
            new SubqueryTableSource(new SelectStatement(null, body, null, null), alias ?? AstSecurityVisitor.InsertCheckAlias), where, null, null));

    [Fact]
    public void InsertCheckOption_CountsTheRowSelects_OfASetOperationSource()
    {
        var body = new SetOperationQuery(new SetOperationQuery(Row(), SetOperator.Union, false, Row()), SetOperator.Union, false, Row());
        Assert.Equal(3, InsertCheckOption.ExpectedRows(Insert(Check(), body)));
        Assert.Equal(1, InsertCheckOption.ExpectedRows(Insert(Check(), Row())));
    }

    [Fact]
    public void InsertCheckOption_FindsTheCheckPredicate_OnEitherSideOfAnAnd_AndInParentheses()
    {
        var other = Eq(Col("a"), Int(2));
        Assert.Equal(1, InsertCheckOption.ExpectedRows(Insert(new BinaryExpression(Check(), BinaryOperator.And, other), Row())));
        Assert.Equal(1, InsertCheckOption.ExpectedRows(Insert(new BinaryExpression(other, BinaryOperator.And, Check()), Row())));
        Assert.Equal(1, InsertCheckOption.ExpectedRows(Insert(new ParenthesizedExpression(Check()), Row())));
    }

    [Fact]
    public void InsertCheckOption_IsNull_WithoutAnInsertCheckPredicate()
    {
        var other = Eq(Col("a"), Int(2));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(other, Row())));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(new BinaryExpression(other, BinaryOperator.And, other), Row())));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(new BinaryExpression(Check(), BinaryOperator.Or, other), Row())));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(Check(SecurityScope.Root), Row())));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(null, Row())));
        Assert.Null(InsertCheckOption.ExpectedRows(Insert(Check(), Row(), new SqlIdentifier("other", IsQuoted: true))));
    }

    // ---- SqliteTypeAffinity ----

    [Theory]
    [InlineData(SqliteTypeAffinity.Kind.Text, "TEXT")]
    [InlineData(SqliteTypeAffinity.Kind.Whole, "INTEGER")]
    [InlineData(SqliteTypeAffinity.Kind.Real, "REAL")]
    [InlineData(SqliteTypeAffinity.Kind.Blob, "BLOB")]
    [InlineData(SqliteTypeAffinity.Kind.Numeric, "NUMERIC")]
    public void CastKeyword_IsTheClosedSetOfAffinityKeywords(SqliteTypeAffinity.Kind kind, string keyword)
    {
        Assert.Equal(keyword, SqliteTypeAffinity.Keyword(kind));
    }
}
